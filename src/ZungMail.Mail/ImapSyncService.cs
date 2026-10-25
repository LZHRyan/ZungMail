using System.Collections.Concurrent;
using System.Diagnostics;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using CoreFlags = ZungMail.Core.Domain.MessageFlags;
using MailFolder = ZungMail.Core.Domain.MailFolder;

namespace ZungMail.Mail;

/// <summary>
/// 基于 MailKit 的 IMAP 增量同步引擎（FR-03）。
/// 增量游标为 <c>folder.last_max_uid</c>：只拉取 <c>UID &gt; last_max_uid</c> 的邮件；
/// <c>UIDVALIDITY</c> 变化时重置文件夹并全量重同步。
/// </summary>
public sealed class ImapSyncService : IMailSyncService
{
    /// <summary>单次 FETCH 的 UID 批量大小。</summary>
    private const int FetchBatchSize = 200;

    /// <summary>首次同步（last_max_uid = 0）时最多拉取的最新邮件数，避免十几万封邮箱一次拖死。</summary>
    private const int MaxInitialFetch = 2000;

    /// <summary>每次同步校准最近这些邮件的标志位（其它设备上的已读/星标变更）。</summary>
    private const int FlagCalibrationWindow = 300;

    /// <summary>
    /// 「正文预取窗口」内每个文件夹单次同步最多补下的正文数量。
    /// 窗口较大的邮箱首轮会分批补齐，之后新邮件基本都能在同一次同步内下载完。
    /// </summary>
    private const int MaxBodiesPerSync = 50;

    /// <summary>
    /// 单轮同步（一个账户）所有文件夹合计的正文预取上限。
    /// 全量同步会把每个可选文件夹都过一遍，按「每文件夹 50 封」算，8 个文件夹就是 400 封全文——
    /// 线上实测「已处理」一个文件夹 50 封正文就花了 49.9 秒，整轮被拖到 60 秒以上。
    /// 收件箱优先用掉自己的 50 封额度，其余文件夹共享这个总预算，剩下的下一轮继续补。
    /// </summary>
    private const int MaxBodiesPerScope = 80;

    /// <summary>
    /// 全量同步时「收件箱以外」的文件夹单轮最多补下多少正文。
    /// 冷文件夹（已处理 / 已归档）的正文按需下载即可，这里只做少量补齐；
    /// 实测这些文件夹每封全文约 0.6–1 秒，给到 50 封就是 50 秒的启动等待。
    /// </summary>
    private const int MaxBodiesPerColdFolder = 15;

    /// <summary>FETCH 字段最多降级几次（见 <see cref="_fetchDegradeLevel"/>）。</summary>
    private const int MaxFetchDegradeLevel = 2;

    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly IContactRepository _contacts;
    private readonly ISettingsStore _settings;
    private readonly IAppPaths _paths;
    private readonly IImapClientFactory _clientFactory;
    private readonly IReadOnlyList<IMailIngestHook> _ingestHooks;
    private readonly ILogger<ImapSyncService> _logger;

    /// <summary>
    /// FETCH 字段降级级别（服务器响应格式不合法时逐级精简）：
    /// 0 = 完整字段；1 = 去掉预览文本；2 = 再去掉 BODYSTRUCTURE。仅影响本次会话。
    /// </summary>
    private volatile int _fetchDegradeLevel;

    /// <summary>
    /// 后台预取正文失败的邮件：本轮进程内不再重试，避免个别损坏邮件每轮同步都占满预取配额。
    /// 用户手动选中该邮件时仍会重新尝试（见 <see cref="DownloadBodyAsync"/>）。
    /// </summary>
    private readonly ConcurrentDictionary<long, byte> _bodyPrefetchFailures = new();

    /// <summary>后台轮询的轮次计数（按账户）：决定本轮刷哪些冷文件夹、要不要重新 LIST。</summary>
    private readonly ConcurrentDictionary<long, int> _roundCounters = new();

    /// <summary>正文下载协调：同一封只下一次，并让后台预取给「用户正在看」的请求让路。</summary>
    private readonly BodyDownloadCoordinator _bodyDownloads = new();

    /// <summary>交互下载串行化：连点十几封也只会有一条连接在跑，其余排队（被取消的不再下载）。</summary>
    private readonly SemaphoreSlim _interactiveBodySlot = new(1, 1);

    public ImapSyncService(
        IAccountRepository accounts,
        IFolderRepository folders,
        IMessageRepository messages,
        IContactRepository contacts,
        ISettingsStore settings,
        IAppPaths paths,
        ILogger<ImapSyncService> logger,
        IImapClientFactory? clientFactory = null,
        IEnumerable<IMailIngestHook>? ingestHooks = null)
    {
        _accounts = accounts;
        _folders = folders;
        _messages = messages;
        _contacts = contacts;
        _settings = settings;
        _paths = paths;
        _logger = logger;
        _clientFactory = clientFactory ?? new MailKitImapClientFactory();
        _ingestHooks = ingestHooks?.ToList() ?? new List<IMailIngestHook>();

        // 拉取邮件头会解析主题 / 收发件人，旧式字符集必须先注册，否则中文与俄语等变成乱码
        MailCharsets.EnsureRegistered();
    }

    public event EventHandler<SyncProgress>? ProgressChanged;

    // ---------------- 同步入口 ----------------

    public async Task<IReadOnlyList<SyncOutcome>> SyncAllAsync(bool fullResync = false, CancellationToken ct = default)
        => await SyncAllCoreAsync(fullResync, SyncScope.All, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SyncOutcome>> SyncAllIncrementalAsync(CancellationToken ct = default)
        => await SyncAllCoreAsync(fullResync: false, SyncScope.Incremental, ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<SyncOutcome>> SyncAllCoreAsync(bool fullResync, SyncScope scope, CancellationToken ct)
    {
        var accounts = await _accounts.GetAllAsync(ct).ConfigureAwait(false);
        // 停止使用的账户不参与同步（保留其本地邮件，可在设置中重新启用）。
        // 主账户排最前：一轮同步里它的收件箱与文件夹最先被刷新，用户最关心的账户先出结果。
        accounts = accounts
            .Where(account => account.IsEnabled)
            .OrderByDescending(account => account.IsPrimary)
            .ThenBy(account => account.SortOrder)
            .ThenBy(account => account.Id)
            .ToList();
        var results = new List<SyncOutcome>();

        // 陌生发件人判定器全轮共用：同一发件人在不同账户 / 文件夹里拿到同一个结论
        var strangerJudge = new StrangerSenderJudge(_messages, accounts.Select(account => account.Email));

        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            if (!account.IsEnabled) continue;
            results.Add(await SyncAccountCoreAsync(account.Id, fullResync, scope, ct, strangerJudge).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>前台同步：遍历全部可选文件夹（用户点「刷新」或设置里点同步时的预期）。</summary>
    public Task<SyncOutcome> SyncAccountAsync(long accountId, bool fullResync = false, CancellationToken ct = default)
        => SyncAccountCoreAsync(accountId, fullResync, SyncScope.All, ct);

    /// <summary>只刷收件箱：IMAP IDLE 探测到 EXISTS 变化后的即时同步。</summary>
    public Task<SyncOutcome> SyncInboxAsync(long accountId, CancellationToken ct = default)
        => SyncAccountCoreAsync(accountId, fullResync: false, SyncScope.InboxOnly, ct);

    private async Task<SyncOutcome> SyncAccountCoreAsync(
        long accountId, bool fullResync, SyncScope scope, CancellationToken ct,
        StrangerSenderJudge? strangerJudge = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var account = await _accounts.GetAsync(accountId, ct).ConfigureAwait(false);
        if (account is null || !account.IsEnabled)
            return Failure(accountId, string.Empty, "账户不存在", fullResync, stopwatch);

        if (!account.IsEnabled)
            return Failure(accountId, account.Name, "账户已停用", fullResync, stopwatch);

        if (!account.HasCompleteConfig)
            return Failure(accountId, account.Name, "账户配置不完整", fullResync, stopwatch);

        var foldersSynced = 0;
        var newMessages = 0;
        var updatedFlags = 0;
        var skippedFolders = 0;

        // 本轮正文预取的剩余额度：所有文件夹共享（收件箱排在最前，先吃完自己的份额）
        var bodyBudget = MaxBodiesPerScope;

        // 单账户同步（手动刷新 / IDLE 收信）自己建一个判定器；全量轮次由 SyncAllCoreAsync 传入共享实例
        strangerJudge ??= await CreateStrangerJudgeAsync(ct).ConfigureAwait(false);

        // 后台轮次的计数：决定本轮刷哪些冷文件夹、要不要重新 LIST
        var round = scope == SyncScope.Incremental
            ? _roundCounters.AddOrUpdate(accountId, 1, (_, value) => value + 1)
            : 1;

        IImapSession? client = null;
        try
        {
            Report(accountId, string.Empty, SyncPhase.Connecting, 0, 0);

            client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);

            var refreshFolders = scope != SyncScope.InboxOnly
                && BackgroundSyncPolicy.ShouldRefreshFolderList(round, fullResync);

            if (refreshFolders)
            {
                Report(accountId, string.Empty, SyncPhase.Folders, 0, 0);
                await RefreshFolderListAsync(client, account, ct).ConfigureAwait(false);
            }

            var folders = await _folders.GetByAccountAsync(accountId, ct).ConfigureAwait(false);
            var selectable = folders.Where(folder => folder.IsSelectable).ToList();

            // 首次启动（本地还没有文件夹记录）时 IDLE 可能先于首轮全量同步触发：补一次 LIST 再找收件箱
            if (scope == SyncScope.InboxOnly && selectable.All(folder => folder.Kind != FolderKind.Inbox))
            {
                await RefreshFolderListAsync(client, account, ct).ConfigureAwait(false);
                folders = await _folders.GetByAccountAsync(accountId, ct).ConfigureAwait(false);
                selectable = folders.Where(folder => folder.IsSelectable).ToList();
            }

            IReadOnlyList<MailFolder> plan = scope switch
            {
                SyncScope.InboxOnly => selectable.Where(folder => folder.Kind == FolderKind.Inbox).Take(1).ToList(),
                SyncScope.Incremental => BackgroundSyncPolicy.SelectFoldersForRound(
                    selectable, round, folder => folder.Kind),
                _ => selectable
            };

            foreach (var folder in plan)
            {
                ct.ThrowIfCancellationRequested();

                // 后台轮次的预算：用满就收尾，剩下的文件夹下一轮接着刷（避免一轮拖成几分钟）
                if (scope == SyncScope.Incremental && stopwatch.Elapsed > BackgroundSyncPolicy.RoundBudget)
                {
                    skippedFolders = plan.Count - foldersSynced;
                    _logger.LogInformation(
                        "账户 {Account} 后台轮询已用 {Elapsed:F1}s（预算 {Budget}s），本轮跳过 {Skipped} 个文件夹，下一轮继续",
                        account.Name, stopwatch.Elapsed.TotalSeconds, BackgroundSyncPolicy.RoundBudget.TotalSeconds, skippedFolders);
                    break;
                }

                // 长连接会被部分服务商（263 等）按空闲/流量策略主动断开，
                // 每个文件夹同步前先探活，必要时重连，避免后续文件夹全部失败。
                if (!client.IsConnected)
                {
                    _logger.LogInformation("IMAP 连接已断开，重连后继续同步文件夹 {Folder}", folder.FullName);
                    client.Dispose();
                    client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
                }

                for (var attempt = 0; ; attempt++)
                {
                    var folderWatch = Stopwatch.StartNew();
                    try
                    {
                        var result = await SyncFolderAsync(
                            client, account, folder, fullResync, scope, bodyBudget, strangerJudge, ct).ConfigureAwait(false);
                        bodyBudget -= result.BodiesPrefetched;
                        newMessages += result.Added;
                        updatedFlags += result.FlagsUpdated;
                        foldersSynced++;
                        LogFolderTiming(account, folder, folderWatch.Elapsed, result.Added);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (ImapProtocolException ex) when (attempt < MaxFetchDegradeLevel)
                    {
                        // 服务器响应格式不合法（预览文本 / BODYSTRUCTURE 等）：逐级精简 FETCH 字段后重连重试
                        _logger.LogWarning(
                            ex, "同步文件夹 {Folder} 时服务器响应格式异常，降级 FETCH 字段（级别 {Level}）后重试",
                            folder.FullName, _fetchDegradeLevel + 1);
                        DegradeFetchItems();
                        client.Dispose();
                        client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (attempt == 0 && IsConnectionLost(ex))
                    {
                        // 同步过程中连接被服务端断开：重连后重试一次该文件夹（NFR-R1）
                        _logger.LogInformation(ex, "同步文件夹 {Folder} 时连接中断，重连后重试一次", folder.FullName);
                        client.Dispose();
                        client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 单个文件夹失败不影响其它文件夹（NFR-R1：失败可重试且不丢数据）
                        _logger.LogWarning(ex, "同步文件夹 {Folder} 失败（耗时 {Elapsed:F1}s）：{Message}",
                            folder.FullName, folderWatch.Elapsed.TotalSeconds, ex.Message);
                        break;
                    }
                }
            }

            Report(accountId, string.Empty, SyncPhase.Completed, 0, 0);
            LogRoundSummary(account, scope, foldersSynced, skippedFolders, newMessages, updatedFlags, stopwatch.Elapsed);
            return new SyncOutcome
            {
                AccountId = accountId,
                AccountName = account.Name,
                FoldersSynced = foldersSynced,
                NewMessages = newMessages,
                UpdatedFlags = updatedFlags,
                FullResync = fullResync,
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            Report(accountId, string.Empty, SyncPhase.Cancelled, 0, 0);
            return new SyncOutcome
            {
                AccountId = accountId,
                AccountName = account.Name,
                FoldersSynced = foldersSynced,
                NewMessages = newMessages,
                UpdatedFlags = updatedFlags,
                Duration = stopwatch.Elapsed,
                Error = "同步已取消"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "账户 {Account} 同步失败", account.Name);
            Report(accountId, string.Empty, SyncPhase.Failed, 0, 0);
            return Failure(accountId, account.Name, ErrorText(ex), fullResync, stopwatch, foldersSynced, newMessages, updatedFlags);
        }
        finally
        {
            client?.Dispose();
        }
    }

    // ---------------- 文件夹列表 ----------------

    private async Task RefreshFolderListAsync(IImapSession client, Account account, CancellationToken ct)
    {
        var remoteFolders = await client.GetFoldersAsync(ct).ConfigureAwait(false);
        if (remoteFolders.Count == 0) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var remote in remoteFolders)
        {
            var attributes = remote.Attributes;
            var selectable = (attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) == 0;

            await _folders.UpsertAsync(new MailFolder
            {
                AccountId = account.Id,
                FullName = remote.FullName,
                DisplayName = remote.Name,
                Kind = MailkitMapping.InferKind(remote.FullName, remote.Name, attributes),
                IsSelectable = selectable
            }, ct).ConfigureAwait(false);

            seen.Add(remote.FullName);
        }

        // 服务器端已不存在的文件夹同步删除
        var local = await _folders.GetByAccountAsync(account.Id, ct).ConfigureAwait(false);
        foreach (var folder in local)
        {
            if (seen.Contains(folder.FullName)) continue;
            _logger.LogInformation("文件夹 {Folder} 已从服务器移除", folder.FullName);
            await _folders.DeleteAsync(folder.Id, ct).ConfigureAwait(false);
        }
    }

    // ---------------- 单文件夹同步 ----------------

    private async Task<(int Added, int FlagsUpdated, int BodiesPrefetched)> SyncFolderAsync(
        IImapSession client, Account account, MailFolder folder, bool fullResync, SyncScope scope, int bodyBudget,
        StrangerSenderJudge? strangerJudge, CancellationToken ct)
    {
        var remote = client.TryGetFolder(folder.FullName);
        if (remote is null)
        {
            _logger.LogWarning("服务器上找不到文件夹 {Folder}", folder.FullName);
            return (0, 0, 0);
        }

        // 单文件夹耗时明细：定位「某个文件夹要几十秒」用（线上出现过「已处理」一轮 49.9 秒）
        var folderClock = Stopwatch.StartNew();
        var cursorBefore = folder.LastMaxUid;
        var headerBatches = 0;
        var flagBatches = 0;

        // 全量重建（UIDVALIDITY 变化或手动全量）会把行删掉重来，此时判定「陌生」只会得到
        // 「全都是陌生人」的错误结论，因此重建的文件夹不参与判定（规格「判定时机与存量数据边界」）。
        var resetTriggered = false;

        await remote.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        try
        {
            var uidValidity = remote.UidValidity;

            if (folder.UidValidity != 0 && uidValidity != 0 && folder.UidValidity != uidValidity)
            {
                // UIDVALIDITY 变化：旧 UID 全部失效，必须全量重同步
                _logger.LogInformation(
                    "文件夹 {Folder} 的 UIDVALIDITY 由 {Old} 变为 {New}，触发全量重同步", folder.FullName, folder.UidValidity, uidValidity);
                await _folders.ResetFolderAsync(folder.Id, uidValidity, ct).ConfigureAwait(false);
                folder.LastMaxUid = 0;
                resetTriggered = true;
            }
            else if (fullResync && folder.LastMaxUid != 0)
            {
                await _folders.ResetFolderAsync(folder.Id, uidValidity != 0 ? uidValidity : folder.UidValidity, ct).ConfigureAwait(false);
                folder.LastMaxUid = 0;
                resetTriggered = true;
            }

            // 陌生发件人判定：只对来信、只对增量新增的邮件。「已发送 / 草稿」是自己写的，不判定。
            var judgeStrangers = strangerJudge is not null
                                 && !resetTriggered
                                 && folder.Kind is not (FolderKind.Sent or FolderKind.Drafts);
            var strangerAddresses = new HashSet<string>(StringComparer.Ordinal);
            var strangerMessages = 0;

            // 只读取「校准窗口尾部 + 游标之后」的 UID：按序位区间 FETCH，不枚举整个文件夹，
            // 十万级以上的邮箱也不会产生全量 UID 列表的内存与网络开销。
            var (newUids, recentUids) = await CollectUidWindowsAsync(remote, folder, ct).ConfigureAwait(false);

            var maxUid = recentUids.Count > 0 ? recentUids[^1] : 0u;
            if (maxUid == 0)
            {
                await UpdateCursorAsync(folder, uidValidity, 0, remote, ct).ConfigureAwait(false);
                _logger.LogDebug(
                    "文件夹同步明细 {Folder}：空文件夹或没有 UID，耗时 {Elapsed:F1}s", folder.FullName, folderClock.Elapsed.TotalSeconds);
                return (0, 0, 0);
            }

            var added = 0;
            var flagsUpdated = 0;

            // 1) 新邮件：只对 UID > last_max_uid 的做完整 FETCH（ENVELOPE / INTERNALDATE / BODYSTRUCTURE / 附加头）
            if (newUids.Count > 0)
            {
                var processed = 0;
                var pendingContacts = new List<EmailAddress>();

                foreach (var chunk in Chunk(newUids, FetchBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    headerBatches++;

                    var summaries = await remote
                        .FetchAsync(chunk, CurrentSummaryItems, MimeMapper.ExtraHeaders, ct)
                        .ConfigureAwait(false);

                    var fresh = new List<EmailDetail>();
                    foreach (var summary in summaries)
                    {
                        // 服务器已标记 \Deleted 的邮件不再拉回本地：
                        // 既避免本机删除后因 EXPUNGE 未生效而「复活」，也跟随其它客户端的删除。
                        var serverFlags = summary.Flags ?? MailKit.MessageFlags.None;
                        if ((serverFlags & MailKit.MessageFlags.Deleted) != 0) continue;

                        var detail = MimeMapper.ToDetail(account.Id, folder.Id, summary.Uid, summary);
                        fresh.Add(detail);
                        pendingContacts.Add(new EmailAddress(detail.FromEmail, detail.FromName));
                        pendingContacts.AddRange(detail.To);
                        pendingContacts.AddRange(detail.Cc);
                    }

                    if (fresh.Count > 0)
                    {
                        // 判定必须在本批写库之前：写进去之后再查，同一批发件人的第二封就会被
                        // 刚入库的第一封判成熟人（判定器同时按地址缓存结论，跨 chunk 也一致）。
                        if (judgeStrangers)
                        {
                            await strangerJudge!
                                .DecideAsync(fresh.Select(detail => detail.FromEmail), ct)
                                .ConfigureAwait(false);

                            foreach (var detail in fresh)
                            {
                                if (!strangerJudge!.IsStranger(detail.FromEmail)) continue;
                                detail.IsStranger = true;
                                strangerMessages++;
                                strangerAddresses.Add(StrangerSenderPolicy.Normalize(detail.FromEmail));
                            }
                        }

                        await _messages.UpsertRangeAsync(fresh, ct).ConfigureAwait(false);
                        await RunIngestHooksAsync(fresh, ct).ConfigureAwait(false);
                        added += fresh.Count;
                    }

                    processed += chunk.Count;
                    Report(account.Id, folder.Title, SyncPhase.Headers, Math.Min(processed, newUids.Count), newUids.Count);
                }

                if (pendingContacts.Count > 0)
                {
                    await CollectContactsAsync(pendingContacts, ct).ConfigureAwait(false);
                }
            }

            // 2) 标志位校准：最近 N 封只取 FLAGS（比整份 ENVELOPE 便宜一个数量级）。
            //    这一步以前和「新邮件」共用一次完整 FETCH，导致没有新信的轮次也要为
            //    300 封邮件拉全套摘要——单文件夹 20 秒上下，是「同步不及时」的主要开销。
            var calibration = recentUids.Where(uid => uid <= folder.LastMaxUid).ToList();
            var calibrationCount = calibration.Count;
            if (calibration.Count > 0)
            {
                var calibrationClock = Stopwatch.StartNew();
                var snapshotFloor = recentUids[0] > 0 ? recentUids[0] - 1 : 0u;
                var localFlags = (await _messages.GetFlagsAsync(folder.Id, snapshotFloor, ct).ConfigureAwait(false))
                    .GroupBy(x => x.Uid)
                    .ToDictionary(g => g.Key, g => (g.First().Id, g.First().Flags));
                var localFlagsMs = calibrationClock.ElapsedMilliseconds;

                var pendingFlags = new List<(long Id, CoreFlags Flags)>();
                var processed = 0;

                foreach (var chunk in Chunk(calibration, FetchBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    flagBatches++;

                    var batchClock = Stopwatch.StartNew();
                    var summaries = await remote
                        .FetchAsync(chunk, MessageSummaryItems.Flags, null, ct)
                        .ConfigureAwait(false);

                    // 单批 FLAGS 超过 2 秒就记一笔：这类「服务器对大文件夹的 UID FETCH 很慢」只能靠日志看出来
                    if (batchClock.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        _logger.LogInformation(
                            "标志位校准单批偏慢 {Folder}：{Count} 封耗时 {Elapsed:F1}s（本轮本地查询 {Local}ms）",
                            folder.FullName, chunk.Count, batchClock.Elapsed.TotalSeconds, localFlagsMs);
                    }

                    foreach (var summary in summaries)
                    {
                        if (summary.Flags is null) continue;

                        var remoteFlags = MailkitMapping.ToLocalFlags(summary.Flags);

                        // 快照里已经带主键：命中即直接进待更新列表，不再对每封变化的邮件各查一次库
                        // （大文件夹一轮能有两三百封变化，机械盘上就是每轮多几秒的同步时间）
                        if (!localFlags.TryGetValue(summary.Uid, out var current) || current.Flags == remoteFlags)
                            continue;

                        pendingFlags.Add((current.Id, remoteFlags));
                    }

                    if (pendingFlags.Count > 0)
                    {
                        await _messages.UpdateFlagsRangeAsync(pendingFlags, ct).ConfigureAwait(false);
                        flagsUpdated += pendingFlags.Count;
                        pendingFlags.Clear();
                    }

                    processed += chunk.Count;
                    Report(account.Id, folder.Title, SyncPhase.Flags, Math.Min(processed, calibration.Count), calibration.Count);
                }
            }

            // 正文预取：窗口内的邮件在同步时补齐正文与附件，窗口外的留到阅读时按需下载。
            // 后台轮询只给收件箱预取；全量同步虽然每个文件夹都预取，但共享本轮总预算，
            // 不会出现「每个文件夹各下 50 封全文」把一轮拖到分钟级（线上实测单文件夹就 49.9 秒）。
            var bodiesPrefetched = 0;
            if (BackgroundSyncPolicy.ShouldPrefetchBodies(scope, folder.Kind) && bodyBudget > 0)
            {
                bodiesPrefetched = await PrefetchBodiesAsync(account, remote, folder, bodyBudget, ct).ConfigureAwait(false);
            }

            await UpdateCursorAsync(folder, uidValidity, maxUid, remote, ct).ConfigureAwait(false);

            // 陌生发件人：单独一行日志，实机核对「列表里的陌生角标数」对不对时直接看这里
            if (strangerMessages > 0)
            {
                _logger.LogInformation(
                    "文件夹 {Folder} 新增陌生发件人来信：{Addresses} 个地址 / {Messages} 封",
                    folder.FullName, strangerAddresses.Count, strangerMessages);
            }

            // 明细：某一轮「没有新邮件却花掉几十秒」时，这行能直接看出是校准窗口太大还是服务器慢
            var elapsed = folderClock.Elapsed.TotalSeconds;
            var syncDetail = $"uidValidity={uidValidity} 游标 {cursorBefore}→{maxUid}，新 UID {newUids.Count}，" +
                             $"校准 {calibrationCount}，新增 {added}，标志 {flagsUpdated}，正文 {bodiesPrefetched}，" +
                             $"陌生 {strangerMessages}，FETCH {headerBatches}+{flagBatches} 批，耗时 {elapsed:F1}s";

            if (added == 0 && elapsed >= 10)
            {
                _logger.LogWarning("文件夹同步偏慢 {Folder}：{Detail}", folder.FullName, syncDetail);
            }
            else if (elapsed >= 3)
            {
                // 正常文件夹都是 2 秒级：超过 3 秒就记 Information，用户把日志发过来就能直接看
                _logger.LogInformation("文件夹同步偏慢 {Folder}：{Detail}", folder.FullName, syncDetail);
            }
            else
            {
                _logger.LogDebug("文件夹同步明细 {Folder}：{Detail}", folder.FullName, syncDetail);
            }

            return (added, flagsUpdated, bodiesPrefetched);
        }
        finally
        {
            try
            {
                await remote.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 连接已被服务端断开时 CloseAsync 会抛 ServiceNotConnectedException；
                // 若直接向上抛会掩盖同步过程中真正的失败原因，故只记录不重抛。
                _logger.LogDebug(ex, "关闭文件夹 {Folder} 失败", folder.FullName);
            }
        }
    }

    /// <summary>
    /// 计算本轮需要同步的 UID：<paramref name="recentUids"/> 为标志位校准窗口（序列末尾
    /// <see cref="FlagCalibrationWindow"/> 封），<paramref name="newUids"/> 为 <c>UID &gt; last_max_uid</c>
    /// 的新邮件（首次同步最多 <see cref="MaxInitialFetch"/> 封）。
    /// <para>
    /// 全部通过序位区间 FETCH 获取，只触达窗口与新增部分；UID 随到达顺序递增，
    /// 因此无需 <c>SEARCH ALL</c> 也能定位新增边界。
    /// </para>
    /// </summary>
    private async Task<(List<uint> NewUids, List<uint> RecentUids)> CollectUidWindowsAsync(
        IImapFolderSession remote, MailFolder folder, CancellationToken ct)
    {
        var recentUids = new List<uint>();
        var newUids = new List<uint>();

        var total = remote.Count;
        if (total <= 0) return (newUids, recentUids);

        // 1) 校准窗口：只取末尾 N 封，用于对齐其它设备上的已读 / 星标变更。
        var recentStart = Math.Max(0, total - FlagCalibrationWindow);
        for (var start = recentStart; start < total; start += FetchBatchSize)
        {
            recentUids.AddRange(await remote
                .FetchUidsByIndexAsync(start, Math.Min(FetchBatchSize, total - start), ct)
                .ConfigureAwait(false));
        }

        if (recentUids.Count == 0) return (newUids, recentUids);

        // 2) 新邮件：从末尾向前回溯，直到跨过游标或达到首次同步上限。
        //    常见场景（无新邮件、仅少量新邮件）在校准窗口内即可确定边界，不产生额外往返。
        if (recentUids[0] > folder.LastMaxUid)
        {
            var remaining = folder.LastMaxUid == 0
                ? Math.Max(0, MaxInitialFetch - recentUids.Count)
                : int.MaxValue;

            var index = recentStart - 1;
            while (index >= 0 && newUids.Count < remaining)
            {
                var start = Math.Max(0, index - FetchBatchSize + 1);
                var batch = await remote
                    .FetchUidsByIndexAsync(start, index - start + 1, ct)
                    .ConfigureAwait(false);

                newUids.AddRange(batch.Where(uid => uid > folder.LastMaxUid));

                // batch 按序位升序；首条已在游标内说明边界已找到，更早的 UID 都属于已同步区间。
                if (batch.Count == 0 || batch[0] <= folder.LastMaxUid) break;
                index = start - 1;
            }
        }

        newUids.AddRange(recentUids.Where(uid => uid > folder.LastMaxUid));

        var unique = newUids.Distinct().OrderBy(uid => uid).ToList();
        if (folder.LastMaxUid == 0 && unique.Count > MaxInitialFetch)
        {
            unique = unique.Skip(unique.Count - MaxInitialFetch).ToList();
        }

        return (unique, recentUids);
    }

    private async Task UpdateCursorAsync(
        MailFolder folder, uint uidValidity, uint maxUid, IImapFolderSession remote, CancellationToken ct)
    {
        var cursor = Math.Max(folder.LastMaxUid, maxUid);
        await _folders
            .UpdateSyncStateAsync(
                folder.Id,
                uidValidity != 0 ? uidValidity : folder.UidValidity,
                cursor,
                DateTimeOffset.UtcNow,
                ct)
            .ConfigureAwait(false);

        folder.LastMaxUid = cursor;

        try
        {
            await remote.RefreshStatusAsync(ct).ConfigureAwait(false);
            var total = Math.Max(remote.Count, 0);
            var unread = Math.Max(remote.Unread, 0);
            await _folders.UpdateCountsAsync(folder.Id, unread, total, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "获取文件夹 {Folder} 计数失败", folder.FullName);
        }
    }

    /// <summary>新邮件入库后执行本地自动化钩子（规则/分类）。单个钩子失败不影响同步。</summary>
    private async Task RunIngestHooksAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct)
    {
        foreach (var hook in _ingestHooks)
        {
            try
            {
                await hook.OnMessagesIngestedAsync(messages, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "邮件入库后置处理 {Hook} 失败", hook.GetType().Name);
            }
        }
    }

    /// <summary>正文（与附件）落地后执行本地索引钩子。单个钩子失败不影响同步。</summary>
    private async Task RunBodyHooksAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct)
    {
        foreach (var hook in _ingestHooks)
        {
            try
            {
                await hook.OnBodiesDownloadedAsync(messages, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "正文落地后置处理 {Hook} 失败", hook.GetType().Name);
            }
        }
    }

    /// <summary>
    /// 建一个陌生发件人判定器：把全部账户（含停用）的自身地址当成熟人，
    /// 这样自己抄送给自己的副本、以及给自己发信的账户地址不会被判成陌生人。
    /// </summary>
    private async Task<StrangerSenderJudge> CreateStrangerJudgeAsync(CancellationToken ct)
    {
        IReadOnlyList<Account> accounts;
        try
        {
            accounts = await _accounts.GetAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 读账户失败不该阻断收信：退化成「没有自有地址」，判定照旧按通讯录与历史来信走
            _logger.LogDebug(ex, "读取账户地址失败（陌生发件人判定）");
            accounts = Array.Empty<Account>();
        }

        return new StrangerSenderJudge(_messages, accounts.Select(account => account.Email));
    }

    private async Task CollectContactsAsync(IReadOnlyList<EmailAddress> addresses, CancellationToken ct)
    {
        try
        {
            var unique = addresses
                .Where(a => !string.IsNullOrWhiteSpace(a.Address))
                .GroupBy(a => a.Address.Trim().ToLowerInvariant())
                .Select(g => g.First())
                .ToList();

            if (unique.Count > 0) await _contacts.CollectAsync(unique, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "沉淀联系人失败");
        }
    }

    /// <summary>
    /// 正文预取：按设置的时间窗口补齐本文件夹缺失的正文与附件。
    /// 窗口内（最近 N 天）的邮件在同步时完整下载；更早的邮件只留邮件头，
    /// 由阅读窗格选中时通过 <see cref="DownloadBodyAsync"/> 按需下载（NFR-P4）。
    /// <para>
    /// 待下载清单从本地库查（而不是只看本轮新邮件），这样窗口内的历史积压、
    /// 以及上一轮被 <see cref="MaxBodiesPerSync"/> 截断的邮件都能在后续同步里补齐。
    /// </para>
    /// </summary>
    private async Task<int> PrefetchBodiesAsync(
        Account account, IImapFolderSession remote, MailFolder folder, int budget, CancellationToken ct)
    {
        // 垃圾箱 / 垃圾邮件只在阅读时按需下载，避免为即将删除的邮件浪费流量
        if (folder.Kind is FolderKind.Trash or FolderKind.Junk) return 0;

        // 账户级收取策略：继承默认时等同于设置页的「默认收取策略」
        var policy = MailFetchPolicy.Resolve(account.FetchPolicy, _settings.Current.BodyPrefetchDays, DateTimeOffset.UtcNow);
        if (!policy.PrefetchBody) return 0;

        // 本轮剩余预算（所有文件夹共享）：单文件夹上限与剩余额度取小；
        // 收件箱按 MaxBodiesPerSync 走，收件箱以外的冷文件夹每轮只补 MaxBodiesPerColdFolder 封
        var perFolderCap = folder.Kind == FolderKind.Inbox ? MaxBodiesPerSync : MaxBodiesPerColdFolder;
        var limit = Math.Min(perFolderCap, budget);
        if (limit <= 0) return 0;

        var floor = policy.BodyFloorUtc;

        IReadOnlyList<(long Id, uint Uid)> pending;
        try
        {
            pending = await _messages
                .GetPendingBodyDownloadsAsync(folder.Id, floor, limit, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 预取只是锦上添花：查不到就跳过，绝不因此让邮件头同步失败
            _logger.LogDebug(ex, "查询待预取正文失败：文件夹 {Folder}", folder.FullName);
            return 0;
        }

        if (pending.Count == 0) return 0;

        var downloaded = new List<EmailDetail>(pending.Count);
        var yielded = false;
        for (var i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // 用户点开了某封邮件（交互下载在等）：本轮预取让路。
            // 头早就全部入库了，界面不受影响；正文下一轮继续补，交互优先。
            if (_bodyDownloads.HasInteractiveRequests)
            {
                yielded = true;
                _logger.LogInformation(
                    "用户正在查看邮件，本轮正文预取让路：已处理 {Done}/{Total} 封，剩余留到下一轮",
                    i, pending.Count);
                break;
            }

            var (id, uid) = pending[i];
            if (_bodyPrefetchFailures.ContainsKey(id)) continue;

            // 这封正被交互下载：不重复拉，等下一轮（那时它已落库，自然不在待下载清单里）
            if (_bodyDownloads.TryGet(id, out _)) continue;

            try
            {
                var detail = await _messages.GetDetailAsync(id, ct).ConfigureAwait(false);
                if (detail is null) continue;

                detail.Uid = uid; // 以查询结果为准，避免 UIDVALIDITY 重置期间的旧值

                // 走协调器：万一用户此刻也点开了这封，双方共用同一次下载
                var task = _bodyDownloads.GetOrStart(id, () => FetchAndSaveBodyAsync(remote, detail, ct, policy.SaveAttachments));
                downloaded.Add(await task.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单封失败不影响其它邮件；本进程内不再重复尝试
                _bodyPrefetchFailures.TryAdd(id, 0);
                _logger.LogDebug(ex, "预取正文失败：邮件 {Id}", id);
            }

            Report(account.Id, remote.Name, SyncPhase.Bodies, i + 1, pending.Count);
        }

        if (yielded) return downloaded.Count;

        if (downloaded.Count > 0)
        {
            await RunBodyHooksAsync(downloaded, ct).ConfigureAwait(false);
        }

        return downloaded.Count;
    }

    // ---------------- 按需下载 / 回写 ----------------

    /// <summary>
    /// 按需下载单封邮件的正文与附件（用户选中一封还没正文的邮件时走这里）。
    /// <para>
    /// 与后台预取的关系：同一封只下一次（后到的等同一个 Task）；用户请求会登记为「有人在等」，
    /// 后台预取在每封之间检查到就让路。快速连点只会有最多一条交互连接在跑，
    /// 被新选择取代的请求通过 <paramref name="ct"/> 取消。
    /// </para>
    /// </summary>
    public Task<bool> DownloadBodyAsync(long emailId, CancellationToken ct = default)
        => DownloadBodyCoreAsync(emailId, includeAttachments: false, force: false, ct);

    /// <summary>
    /// 按需下载正文。<paramref name="includeAttachments"/> 为 true 时无论账户策略如何都把附件落盘
    /// （用户在附件区点了「下载附件」）。
    /// </summary>
    public Task<bool> DownloadBodyAsync(long emailId, bool includeAttachments, CancellationToken ct = default)
        => DownloadBodyCoreAsync(emailId, includeAttachments, force: false, ct);

    /// <summary>
    /// 强制重下正文：正文区为空时用户点「重新获取正文」走这里。
    /// 与普通按需下载的唯一区别是不认本地「已下载」标记 —— 那种邮件的状态恰恰是
    /// Downloaded + 空正文，否则永远不会再拉一次。
    /// </summary>
    public Task<bool> RedownloadBodyAsync(long emailId, CancellationToken ct = default)
        => DownloadBodyCoreAsync(emailId, includeAttachments: false, force: true, ct);

    private async Task<bool> DownloadBodyCoreAsync(
        long emailId, bool includeAttachments, bool force, CancellationToken ct)
    {
        // 已经下载过：直接返回，不再拉全文（要求顺带拉附件时，附件还没落盘就继续）
        var detail = await _messages.GetDetailAsync(emailId, ct).ConfigureAwait(false);
        if (detail is null) return false;
        if (!force && detail.BodyState == BodyState.Downloaded && !NeedsAttachmentDownload(detail, includeAttachments)) return true;

        // 后台预取正好在下这封：等同一个任务，不另开连接、不重复下载
        if (_bodyDownloads.TryGet(emailId, out var inflight))
        {
            return await AwaitInFlightAsync(emailId, inflight, ct).ConfigureAwait(false);
        }

        using var interactive = _bodyDownloads.EnterInteractive();

        try
        {
            await _interactiveBodySlot.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 队列里就被新的选择取代了：连连接都不用建
            return false;
        }

        try
        {
            // 排队期间：预取可能已经开始下这封，或者它已经下完了
            if (_bodyDownloads.TryGet(emailId, out inflight))
            {
                return await AwaitInFlightAsync(emailId, inflight, ct).ConfigureAwait(false);
            }

            detail = await _messages.GetDetailAsync(emailId, ct).ConfigureAwait(false);
            if (detail is null) return false;
            if (!force && detail.BodyState == BodyState.Downloaded && !NeedsAttachmentDownload(detail, includeAttachments)) return true;

            var account = await _accounts.GetAsync(detail.AccountId, ct).ConfigureAwait(false);
            if (account is null) return false;

            var folder = await _folders.GetAsync(detail.FolderId, ct).ConfigureAwait(false);
            if (folder is null) return false;

            var stopwatch = Stopwatch.StartNew();
            // 交互下载也遵守账户策略：仅收取正文的账户，附件要用户单独点了才落盘
            var saveAttachments = includeAttachments
                || MailFetchPolicy.Resolve(account.FetchPolicy, _settings.Current.BodyPrefetchDays, DateTimeOffset.UtcNow).SaveAttachments;

            var task = _bodyDownloads.GetOrStart(
                emailId,
                () => DownloadBodyOverNewConnectionAsync(emailId, account, folder, detail, ct, saveAttachments));

            var ok = await AwaitInFlightAsync(emailId, task, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "交互下载正文：邮件 {Id}{Result}，耗时 {Elapsed:F1}s",
                emailId, ok ? string.Empty : "（失败）", stopwatch.Elapsed.TotalSeconds);
            return ok;
        }
        finally
        {
            _interactiveBodySlot.Release();
        }
    }

    /// <summary>等一个正在进行的正文下载：只取消「等」这件事，不打断别人已经开始的下载。</summary>
    private static async Task<bool> AwaitInFlightAsync(long emailId, Task<EmailDetail> task, CancellationToken ct)
    {
        try
        {
            var detail = await task.WaitAsync(ct).ConfigureAwait(false);
            return detail?.BodyState == BodyState.Downloaded;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 共享下载被发起方取消（用户切走了）：这次交互下载视为失败，等下次同步补齐
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>要求顺带下载附件时，判断这封邮件是否还有附件没落盘。</summary>
    private static bool NeedsAttachmentDownload(EmailDetail detail, bool includeAttachments)
        => includeAttachments && detail.Attachments.Any(a => !a.IsDownloaded);

    /// <summary>用一条独立连接把整封邮件拉下来并落库（交互下载专用，与同步会话分离）。</summary>
    private async Task<EmailDetail> DownloadBodyOverNewConnectionAsync(
        long emailId, Account account, MailFolder folder, EmailDetail detail, CancellationToken ct, bool saveAttachments)
    {
        try
        {
            using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            var remote = client.TryGetFolder(folder.FullName)
                ?? throw new FolderNotFoundException("服务器上找不到邮件所在文件夹", folder.FullName);

            await remote.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            try
            {
                var result = await FetchAndSaveBodyAsync(remote, detail, ct, saveAttachments).ConfigureAwait(false);
                _bodyPrefetchFailures.TryRemove(emailId, out _);
                return result;
            }
            finally
            {
                await remote.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "下载正文失败：邮件 {Id}", emailId);
            throw;
        }
    }

    /// <summary>
    /// 下载单封邮件的完整内容并落库，返回已标记为「正文已下载」的详情。
    /// <paramref name="saveAttachments"/> 为 false 时（账户策略「仅收取正文」）只写正文，附件条目记为未下载。
    /// </summary>
    private async Task<EmailDetail> FetchAndSaveBodyAsync(
        IImapFolderSession remote, EmailDetail detail, CancellationToken ct, bool saveAttachments = true)
    {
        // 整封原文（含附件字节）由 MimeMessage 持有，use 后必须释放，否则预取一轮就攒下几十 MB
        using var message = await remote.GetMessageAsync(detail.Uid, ct).ConfigureAwait(false);
        var body = await MimeMapper.ExtractBodyAsync(message, _paths, detail, ct, saveAttachments).ConfigureAwait(false);
        await _messages.SaveBodyAsync(detail.Id, body.Html, body.Text, body.Attachments, ct).ConfigureAwait(false);

        // 退信：失败收件人 / 状态码只在 delivery-status 段里，正文已下载就顺带解析（不额外发请求）；
        // 顺带兜住「BODYSTRUCTURE 不可用、发件人主题也没命中」的 DSN（例如内部转发过的退信）。
        var bounce = BounceDsnParser.Parse(message);
        if (bounce is not null)
        {
            detail.IsBounce = true;
            detail.BounceRecipient = bounce.Recipient;
            detail.BounceStatus = bounce.Status;
            detail.BounceDetail = bounce.Detail;
        }

        if (detail.IsBounce && detail.Id > 0)
        {
            await _messages
                .UpdateBounceAsync(detail.Id, true, detail.BounceRecipient, detail.BounceStatus, detail.BounceDetail, ct)
                .ConfigureAwait(false);
        }

        detail.BodyHtml = body.Html;
        detail.BodyText = body.Text;
        detail.Attachments = body.Attachments;
        detail.BodyState = BodyState.Downloaded;
        return detail;
    }

    public async Task<bool> PushFlagsAsync(long emailId, CoreFlags flags, CancellationToken ct = default)
    {
        var detail = await _messages.GetDetailAsync(emailId, ct).ConfigureAwait(false);
        if (detail is null) return false;

        var account = await _accounts.GetAsync(detail.AccountId, ct).ConfigureAwait(false);
        if (account is null) return false;

        var folder = await _folders.GetAsync(detail.FolderId, ct).ConfigureAwait(false);
        if (folder is null) return false;

        try
        {
            using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            var remote = client.TryGetFolder(folder.FullName);
            if (remote is null) return false;
            await remote.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            try
            {
                var uid = detail.Uid;
                var summaries = await remote.FetchAsync(new[] { uid }, MessageSummaryItems.Flags, null, ct).ConfigureAwait(false);

                // 服务器上已不存在该邮件（例如删除已生效）：视为回写完成，避免队列永久卡在失败态。
                if (summaries.Count == 0) return true;

                var current = summaries[0].Flags ?? MailKit.MessageFlags.None;

                var target = MailkitMapping.ToImapFlags(flags);
                var toAdd = target & ~current;
                var toRemove = (current & MailkitMapping.ManagedFlags) & ~target;

                if (toAdd != MailKit.MessageFlags.None)
                    await remote.AddFlagsAsync(uid, toAdd, ct).ConfigureAwait(false);
                if (toRemove != MailKit.MessageFlags.None)
                    await remote.RemoveFlagsAsync(uid, toRemove, ct).ConfigureAwait(false);

                // 删除语义需要 EXPUNGE 才真正从服务器移除；不支持 UIDPLUS 时至少保留 \Deleted 标记，
                // 本地同步会跳过带 \Deleted 的邮件，不会再次出现在列表里。
                if ((flags & CoreFlags.Deleted) != 0)
                {
                    try
                    {
                        if (client.SupportsUidPlus)
                        {
                            await remote.ExpungeAsync(new[] { uid }, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "EXPUNGE 失败，保留 \\Deleted 标记：邮件 {Id}", emailId);
                    }
                }

                return true;
            }
            finally
            {
                await remote.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回写标志位失败：邮件 {Id}", emailId);
            return false;
        }
    }

    /// <summary>
    /// 批量回写标志位：按（账户, 文件夹）合并连接，逐个 UID 对齐服务器标志。
    /// 供「全部标为已读」这类批量操作使用，避免逐封重新建连。
    /// </summary>
    public async Task<int> PushFlagsBatchAsync(
        IReadOnlyList<(long EmailId, CoreFlags Flags)> updates, CancellationToken ct = default)
    {
        if (updates.Count == 0) return 0;

        var items = new List<(long AccountId, long FolderId, uint Uid, CoreFlags Flags)>(updates.Count);
        foreach (var (emailId, flags) in updates)
        {
            var detail = await _messages.GetDetailAsync(emailId, ct).ConfigureAwait(false);
            if (detail is not null) items.Add((detail.AccountId, detail.FolderId, detail.Uid, flags));
        }

        var pushed = 0;
        foreach (var group in items.GroupBy(i => new { i.AccountId, i.FolderId }))
        {
            var account = await _accounts.GetAsync(group.Key.AccountId, ct).ConfigureAwait(false);
            var folder = await _folders.GetAsync(group.Key.FolderId, ct).ConfigureAwait(false);
            if (account is null || folder is null) continue;

            try
            {
                using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
                var remote = client.TryGetFolder(folder.FullName);
                if (remote is null) continue;
                await remote.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
                try
                {
                    var uids = group.Select(i => i.Uid).ToList();
                    var summaries = await remote.FetchAsync(uids, MessageSummaryItems.Flags, null, ct).ConfigureAwait(false);
                    var current = summaries
                        .ToDictionary(s => s.Uid, s => s.Flags ?? MailKit.MessageFlags.None);

                    foreach (var item in group)
                    {
                        ct.ThrowIfCancellationRequested();

                        var uid = item.Uid;
                        var existing = current.TryGetValue(uid, out var flags)
                            ? flags
                            : MailKit.MessageFlags.None;

                        var target = MailkitMapping.ToImapFlags(item.Flags);
                        var toAdd = target & ~existing;
                        var toRemove = (existing & MailkitMapping.ManagedFlags) & ~target;

                        if (toAdd != MailKit.MessageFlags.None)
                            await remote.AddFlagsAsync(uid, toAdd, ct).ConfigureAwait(false);
                        if (toRemove != MailKit.MessageFlags.None)
                            await remote.RemoveFlagsAsync(uid, toRemove, ct).ConfigureAwait(false);

                        pushed++;
                    }
                }
                finally
                {
                    await remote.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "批量回写标志位失败：账户 {Account} 文件夹 {Folder}", group.Key.AccountId, group.Key.FolderId);
            }
        }

        return pushed;
    }

    /// <summary>
    /// 将邮件移动到语义文件夹。优先使用服务器的 MOVE；不支持时回退 COPY + \Deleted，
    /// 以便应用层把失败操作安全留在本地待同步队列中重试。
    /// </summary>
    public async Task<MovePushResult> MoveAsync(long emailId, FolderKind destination, CancellationToken ct = default)
    {
        var detail = await _messages.GetDetailAsync(emailId, ct).ConfigureAwait(false);
        // 本地行已不存在：没有可回写的对象，按「已不在」结案
        if (detail is null) return MovePushResult.MessageGone;

        var account = await _accounts.GetAsync(detail.AccountId, ct).ConfigureAwait(false);
        var source = await _folders.GetAsync(detail.FolderId, ct).ConfigureAwait(false);
        var target = await _folders.FindByKindAsync(detail.AccountId, destination, ct).ConfigureAwait(false);
        if (account is null || source is null) return MovePushResult.MessageGone;

        // 目标文件夹不存在或就是当前文件夹：本地状态已经满足意图，不必回写
        if (target is null || source.Id == target.Id) return MovePushResult.MessageGone;

        try
        {
            using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            var remoteSource = client.TryGetFolder(source.FullName);
            var remoteTarget = client.TryGetFolder(target.FullName);
            if (remoteSource is null) return MovePushResult.Retryable;
            if (remoteTarget is null) return MovePushResult.MessageGone; // 目标在服务器上没了：移不过去，结案

            await remoteSource.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            try
            {
                if (client.SupportsMove)
                {
                    await remoteSource.MoveToAsync(detail.Uid, remoteTarget, ct).ConfigureAwait(false);
                }
                else
                {
                    await remoteSource.CopyToAsync(detail.Uid, remoteTarget, ct).ConfigureAwait(false);
                    await remoteSource.AddFlagsAsync(detail.Uid, MailKit.MessageFlags.Deleted, ct).ConfigureAwait(false);
                    if (client.SupportsUidPlus)
                    {
                        await remoteSource.ExpungeAsync(new[] { detail.Uid }, ct).ConfigureAwait(false);
                    }
                }

                return MovePushResult.Moved;
            }
            finally
            {
                await remoteSource.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsMessageGone(ex))
        {
            // 服务器明确拒绝（UID 已失效 / 已被其它客户端移走）：重试一万次也不会成功
            _logger.LogWarning(
                "移动邮件失败（服务器上没有这封了，结案）：邮件 {EmailId} → {Destination}", emailId, destination);
            return MovePushResult.MessageGone;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移动邮件失败：邮件 {EmailId} → {Destination}", emailId, destination);
            return MovePushResult.Retryable;
        }
    }

    /// <summary>
    /// 判断异常是否表示「服务器上已经没有这封邮件」：规则见 <see cref="ImapFailure"/>。
    /// 这类错误重试没有意义，必须结案，否则会在待同步队列里反复重试。
    /// </summary>
    private static bool IsMessageGone(Exception ex) => ImapFailure.IsMessageGone(ex);

    /// <summary>移动到指定账户的任意可写文件夹；仅支持同一账户内的目标（跨账户移动语义不同）。</summary>
    public async Task<MovePushResult> MoveToFolderAsync(
        long emailId, long destinationAccountId, string destinationFullName, CancellationToken ct = default)
    {
        var detail = await _messages.GetDetailAsync(emailId, ct).ConfigureAwait(false);
        if (detail is null || detail.AccountId != destinationAccountId) return MovePushResult.MessageGone;

        var account = await _accounts.GetAsync(detail.AccountId, ct).ConfigureAwait(false);
        var source = await _folders.GetAsync(detail.FolderId, ct).ConfigureAwait(false);
        if (account is null || source is null) return MovePushResult.MessageGone;
        if (source.FullName.Equals(destinationFullName, StringComparison.OrdinalIgnoreCase)) return MovePushResult.MessageGone;

        try
        {
            using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            var remoteSource = client.TryGetFolder(source.FullName);
            var remoteTarget = client.TryGetFolder(destinationFullName);
            if (remoteSource is null) return MovePushResult.Retryable;
            if (remoteTarget is null) return MovePushResult.MessageGone; // 目标文件夹在服务器上没了

            await remoteSource.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            try
            {
                if (client.SupportsMove)
                {
                    await remoteSource.MoveToAsync(detail.Uid, remoteTarget, ct).ConfigureAwait(false);
                }
                else
                {
                    await remoteSource.CopyToAsync(detail.Uid, remoteTarget, ct).ConfigureAwait(false);
                    await remoteSource.AddFlagsAsync(detail.Uid, MailKit.MessageFlags.Deleted, ct).ConfigureAwait(false);
                    if (client.SupportsUidPlus)
                        await remoteSource.ExpungeAsync(new[] { detail.Uid }, ct).ConfigureAwait(false);
                }

                return MovePushResult.Moved;
            }
            finally
            {
                await remoteSource.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsMessageGone(ex))
        {
            _logger.LogWarning(
                "移动到文件夹失败（服务器上没有这封了，结案）：邮件 {EmailId} → {Folder}", emailId, destinationFullName);
            return MovePushResult.MessageGone;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移动到文件夹失败：邮件 {EmailId} → {Folder}", emailId, destinationFullName);
            return MovePushResult.Retryable;
        }
    }

    /// <summary>
    /// 批量移动到同一目标文件夹：按源文件夹分组，每组只用一条连接、一条 MOVE 命令带走整批 UID。
    /// 这是拖拽移动多封邮件的主路径（逐封建连在 20 封以上时能差出十几秒）。
    /// </summary>
    public async Task<IReadOnlyDictionary<long, MovePushResult>> MoveToFolderBatchAsync(
        IReadOnlyList<long> emailIds, long destinationAccountId, string destinationFullName, CancellationToken ct = default)
    {
        var results = new Dictionary<long, MovePushResult>(emailIds.Count);

        // 同一批拖拽通常来自同一个文件夹：理想情况只有一组 → 一条连接 + 一条 MOVE
        var groups = new Dictionary<(long AccountId, long FolderId), (MailFolder Source, List<(long EmailId, uint Uid)> Items)>();

        foreach (var emailId in emailIds)
        {
            var detail = await _messages.GetDetailAsync(emailId, ct).ConfigureAwait(false);
            if (detail is null || detail.AccountId != destinationAccountId)
            {
                results[emailId] = MovePushResult.MessageGone;
                continue;
            }

            var source = await _folders.GetAsync(detail.FolderId, ct).ConfigureAwait(false);
            if (source is null || source.FullName.Equals(destinationFullName, StringComparison.OrdinalIgnoreCase))
            {
                results[emailId] = MovePushResult.MessageGone;
                continue;
            }

            var key = (detail.AccountId, detail.FolderId);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (source, new List<(long, uint)>());
                groups[key] = group;
            }
            group.Items.Add((emailId, detail.Uid));
        }

        foreach (var group in groups.Values)
        {
            var account = await _accounts.GetAsync(group.Source.AccountId, ct).ConfigureAwait(false);
            if (account is null)
            {
                foreach (var item in group.Items) results[item.EmailId] = MovePushResult.MessageGone;
                continue;
            }

            await MoveGroupAsync(account, group.Source, group.Items, destinationFullName, results, ct).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>同一源文件夹下的一批邮件：一条连接内完成整批 MOVE，单封失败再退回逐封。</summary>
    private async Task MoveGroupAsync(
        Account account,
        MailFolder source,
        List<(long EmailId, uint Uid)> items,
        string destinationFullName,
        Dictionary<long, MovePushResult> results,
        CancellationToken ct)
    {
        try
        {
            using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            var remoteSource = client.TryGetFolder(source.FullName);
            var remoteTarget = client.TryGetFolder(destinationFullName);
            if (remoteSource is null)
            {
                foreach (var item in items) results[item.EmailId] = MovePushResult.Retryable;
                return;
            }
            if (remoteTarget is null)
            {
                // 目标文件夹在服务器上没了：重试也不会成功
                foreach (var item in items) results[item.EmailId] = MovePushResult.MessageGone;
                return;
            }

            await remoteSource.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            try
            {
                var uids = items.Select(item => item.Uid).Distinct().ToList();
                try
                {
                    if (client.SupportsMove)
                    {
                        await remoteSource.MoveManyAsync(uids, remoteTarget, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await remoteSource.CopyManyAsync(uids, remoteTarget, ct).ConfigureAwait(false);
                        await remoteSource.AddFlagsManyAsync(uids, MailKit.MessageFlags.Deleted, ct).ConfigureAwait(false);
                        if (client.SupportsUidPlus)
                            await remoteSource.ExpungeAsync(uids, ct).ConfigureAwait(false);
                    }

                    foreach (var item in items) results[item.EmailId] = MovePushResult.Moved;
                    return;
                }
                catch (Exception ex) when (IsMessageGone(ex))
                {
                    // 整批被拒（其中某个 UID 已失效）：退回逐封，能移的照样移走
                    _logger.LogInformation(ex, "批量移动被服务器拒绝，退回逐封：{Folder} → {Destination}", source.FullName, destinationFullName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "批量移动失败，退回逐封：{Folder} → {Destination}", source.FullName, destinationFullName);
                }

                foreach (var item in items)
                {
                    results[item.EmailId] = await MoveOneAsync(client, remoteSource, remoteTarget, item.Uid, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                await remoteSource.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsMessageGone(ex))
        {
            _logger.LogWarning("批量移动失败（服务器上已经没有这些邮件，结案）：{Folder} → {Destination}", source.FullName, destinationFullName);
            foreach (var item in items) results[item.EmailId] = MovePushResult.MessageGone;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "批量移动失败：{Folder} → {Destination}", source.FullName, destinationFullName);
            foreach (var item in items) results[item.EmailId] = MovePushResult.Retryable;
        }
    }

    /// <summary>在已打开的连接上移动单封（批量命令失败后的兜底）。</summary>
    private async Task<MovePushResult> MoveOneAsync(
        IImapSession client, IImapFolderSession source, IImapFolderSession target, uint uid, CancellationToken ct)
    {
        try
        {
            if (client.SupportsMove)
            {
                await source.MoveToAsync(uid, target, ct).ConfigureAwait(false);
            }
            else
            {
                await source.CopyToAsync(uid, target, ct).ConfigureAwait(false);
                await source.AddFlagsAsync(uid, MailKit.MessageFlags.Deleted, ct).ConfigureAwait(false);
                if (client.SupportsUidPlus) await source.ExpungeAsync(new[] { uid }, ct).ConfigureAwait(false);
            }

            return MovePushResult.Moved;
        }
        catch (Exception ex) when (IsMessageGone(ex))
        {
            return MovePushResult.MessageGone;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移动邮件失败：UID {Uid}", uid);
            return MovePushResult.Retryable;
        }
    }

    /// <summary>把已发送邮件追加到服务器「已发送」文件夹（尽力而为，失败不影响发送结果）。</summary>
    public async Task<bool> AppendToSentAsync(Account account, MimeKit.MimeMessage message, CancellationToken ct = default)
    {
        try
        {
            var sent = await _folders.FindByKindAsync(account.Id, FolderKind.Sent, ct).ConfigureAwait(false);
            if (sent is null) return false;

            using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            var remote = client.TryGetFolder(sent.FullName);
            if (remote is null) return false;
            await remote.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            try
            {
                await remote.AppendAsync(message, MailKit.MessageFlags.Seen, ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                await remote.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存到已发送文件夹失败");
            return false;
        }
    }

    /// <summary>
    /// 把草稿追加到服务器「草稿箱」（\Draft），返回服务器分配的 UID；
    /// 服务器不支持 UIDPLUS 或追加失败时返回 null，由调用方退化为「只存本地草稿」。
    /// </summary>
    public async Task<uint?> AppendDraftAsync(Account account, MimeKit.MimeMessage message, CancellationToken ct = default)
    {
        try
        {
            var drafts = await _folders.FindByKindAsync(account.Id, FolderKind.Drafts, ct).ConfigureAwait(false);
            if (drafts is null) return null;

            using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            var remote = client.TryGetFolder(drafts.FullName);
            if (remote is null) return null;
            await remote.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            try
            {
                return await remote.AppendAsync(message, MailKit.MessageFlags.Draft, ct).ConfigureAwait(false);
            }
            finally
            {
                await remote.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存草稿到服务器失败（本地草稿仍然保留）");
            return null;
        }
    }

    /// <summary>彻底删除服务器上的某封信（按文件夹路径 + UID），用于草稿发送成功后清理；失败只记日志。</summary>
    public async Task<bool> DeleteRemoteAsync(Account account, string folderFullName, uint uid, CancellationToken ct = default)
    {
        try
        {
            using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            var remote = client.TryGetFolder(folderFullName);
            if (remote is null) return false;
            await remote.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            try
            {
                await remote.AddFlagsAsync(uid, MailKit.MessageFlags.Deleted, ct).ConfigureAwait(false);
                await remote.ExpungeAsync(new[] { uid }, ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                await remote.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除服务器草稿失败：{Folder}#{Uid}", folderFullName, uid);
            return false;
        }
    }

    // ---------------- 测试连接 ----------------

    public async Task<bool> TestConnectionAsync(Account account, CancellationToken ct = default)
    {
        if (!account.HasCompleteConfig)
        {
            account.ConnectionStatus = "配置不完整";
            return false;
        }

        try
        {
            using (var imap = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false))
            {
                account.ConnectionStatus = imap.SupportsImap4Rev2 ? "IMAP 正常（IMAP4rev2）" : "IMAP 正常";
            }

            using (var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 30_000 })
            {
                await smtp
                    .ConnectAsync(account.SmtpHost, account.SmtpPort, MailkitMapping.ToSocketOptions(account.SmtpSecure), ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(account.Username))
                {
                    if (account.AuthKind == AuthKind.OAuth2)
                        await smtp.AuthenticateAsync(new MailKit.Security.SaslMechanismOAuth2(account.Username, account.Secret), ct).ConfigureAwait(false);
                    else
                        await smtp.AuthenticateAsync(account.Username, account.Secret, ct).ConfigureAwait(false);
                }
                await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
            }

            account.ConnectionStatus = "IMAP / SMTP 均正常";
            return true;
        }
        catch (Exception ex)
        {
            account.ConnectionStatus = ErrorText(ex);
            _logger.LogWarning(ex, "测试连接失败：{Email}", account.Email);
            return false;
        }
    }

    // ---------------- 辅助 ----------------

    /// <summary>
    /// 单文件夹耗时：超过 1.5 秒用 Information 落日志（慢文件夹是「同步不及时」的第一现场），
    /// 快的走 Debug，避免日志被上百个文件夹刷满。
    /// </summary>
    private void LogFolderTiming(Account account, MailFolder folder, TimeSpan elapsed, int added)
    {
        if (elapsed >= TimeSpan.FromSeconds(1.5))
        {
            _logger.LogInformation(
                "账户 {Account} 文件夹 {Folder} 同步耗时 {Elapsed:F1}s（新增 {Added} 封）",
                account.Name, folder.FullName, elapsed.TotalSeconds, added);
        }
        else
        {
            _logger.LogDebug(
                "账户 {Account} 文件夹 {Folder} 同步耗时 {Elapsed:F0}ms（新增 {Added} 封）",
                account.Name, folder.FullName, elapsed.TotalMilliseconds, added);
        }
    }

    /// <summary>每轮同步收尾的统一日志：一眼看出「这轮多久、刷了几个文件夹、新增几封」。</summary>
    private void LogRoundSummary(
        Account account, SyncScope scope, int foldersSynced, int skippedFolders,
        int newMessages, int updatedFlags, TimeSpan elapsed)
    {
        var scopeText = scope switch
        {
            SyncScope.InboxOnly => "仅收件箱",
            SyncScope.Incremental => "后台增量",
            _ => "全部文件夹"
        };

        _logger.LogInformation(
            "账户 {Account} 同步完成（{Scope}）：{Folders} 个文件夹{SkipText}，新增 {New} 封，状态更新 {Flags} 条，耗时 {Elapsed:F1}s",
            account.Name, scopeText, foldersSynced,
            skippedFolders > 0 ? $"，本轮跳过 {skippedFolders} 个" : string.Empty,
            newMessages, updatedFlags, elapsed.TotalSeconds);
    }

    private void Report(long accountId, string folderName, SyncPhase phase, int processed, int total) =>
        ProgressChanged?.Invoke(this, new SyncProgress
        {
            AccountId = accountId,
            FolderName = folderName,
            Phase = phase,
            Processed = processed,
            Total = total
        });

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }

    private static SyncOutcome Failure(
        long accountId, string accountName, string error, bool fullResync, Stopwatch stopwatch,
        int foldersSynced = 0, int newMessages = 0, int updatedFlags = 0) =>
        new()
        {
            AccountId = accountId,
            AccountName = accountName,
            FoldersSynced = foldersSynced,
            NewMessages = newMessages,
            UpdatedFlags = updatedFlags,
            FullResync = fullResync,
            Duration = stopwatch.Elapsed,
            Error = error
        };

    private static string ErrorText(Exception ex) => ex switch
    {
        MailKit.Security.AuthenticationException => "账号或授权码不正确，或该邮箱未开启 IMAP / SMTP 服务",
        MailKit.Security.SslHandshakeException => "SSL 握手失败，请检查端口与加密方式",
        System.Net.Sockets.SocketException => $"无法连接服务器：{ex.Message}",
        TimeoutException => "连接服务器超时",
        FolderNotFoundException => "服务器上找不到该文件夹",
        _ => ex.Message
    };

    /// <summary>判断异常是否由 IMAP 连接中断引起（可重连后重试）。</summary>
    private static bool IsConnectionLost(Exception ex) =>
        ex is ServiceNotConnectedException
            or ImapProtocolException
            or System.IO.IOException
            or System.Net.Sockets.SocketException;

    /// <summary>当前降级级别对应的 FETCH 字段。</summary>
    private MessageSummaryItems CurrentSummaryItems => _fetchDegradeLevel switch
    {
        0 => MimeMapper.SummaryItems,
        1 => MimeMapper.SummaryItemsWithoutPreview,
        _ => MimeMapper.SummaryItemsMinimal
    };

    /// <summary>逐级精简 FETCH 字段（服务器响应格式不合法时使用）。</summary>
    private void DegradeFetchItems()
    {
        if (_fetchDegradeLevel < MaxFetchDegradeLevel) _fetchDegradeLevel++;
    }
}
