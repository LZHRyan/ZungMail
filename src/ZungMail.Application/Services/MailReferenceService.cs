using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Extensions.Logging;

namespace ZungMail.Application.Services;

/// <summary>
/// 货代业务索引服务：把邮件主题与正文里的 Job / SO / B/L / 箱号等单号，
/// 以及截关 / 截补料 / VGM / ETD / ETA 等时效节点抽出来落库；
/// 同时按单号把邮件归并成「一票货」案件，供列表徽标、详情业务卡、结构化搜索与时效看板使用。
/// <para>
/// 索引分两条路径：新邮件入库时即时抽取（只有主题与摘要），
/// 以及后台补扫——补扫会处理历史邮件，并在正文晚于邮件头到达后重扫一次。
/// </para>
/// </summary>
public sealed class MailReferenceService
{
    private readonly IMailReferenceRepository _repository;
    private readonly IShipmentRepository _shipments;
    private readonly IMailShipmentFieldRepository? _shipmentFields;
    private readonly ISettingsStore? _settings;
    private readonly IAppMetadataStore? _metadata;
    private readonly ILogger<MailReferenceService> _logger;

    /// <summary>类型识别规则升级过一次：用它把存量邮件重新排进补扫队列（只跑一次）。</summary>
    private const string RecallBackfillKey = "mail_type_recall_v1";

    /// <summary>运输字段首次上线：同样清一次补扫进度，让存量邮件补出箱型/航线/条款/金额。</summary>
    private const string ShipmentFieldsBackfillKey = "shipment_fields_v1";

    /// <summary>单飞标记：后台补扫与正文下载触发的索引不并发执行，避免重复写锁。</summary>
    private int _indexing;

    public MailReferenceService(
        IMailReferenceRepository repository,
        IShipmentRepository shipments,
        ILogger<MailReferenceService> logger,
        ISettingsStore? settings = null,
        IAppMetadataStore? metadata = null,
        IMailShipmentFieldRepository? shipmentFields = null)
    {
        _repository = repository;
        _shipments = shipments;
        _logger = logger;
        // 设置存储是可选的：只装配 Application 层的单元测试拿不到它，此时按「货代功能全开」处理
        _settings = settings;
        _metadata = metadata;
        _shipmentFields = shipmentFields;
    }

    /// <summary>
    /// 一次性：识别规则升级后让存量邮件重新排队（清空补扫进度），并立刻跑一批。
    /// 只有真的写过 meta 键才清进度，避免每次启动都重扫；开关关闭时不动，等开启后再补。
    /// </summary>
    public async Task<int> EnsureRecallBackfillAsync(CancellationToken ct = default)
    {
        if (_metadata is null || !Flags.AnyRecognition) return 0;

        // 两个一次性的键共用一个「清空补扫进度」动作：清一次就够，谁缺就补谁
        var needsClear = false;
        foreach (var key in new[] { RecallBackfillKey, ShipmentFieldsBackfillKey })
        {
            if (await _metadata.HasAsync(key, ct).ConfigureAwait(false)) continue;

            needsClear = true;
            await _metadata.SetAsync(key, DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);
        }

        if (needsClear)
        {
            await _repository.ClearScanProgressAsync(ct).ConfigureAwait(false);
        }

        return await IndexPendingAsync(500, 20, ct).ConfigureAwait(false);
    }

    /// <summary>当前的货代功能开关（总开关关闭时四项都算关；没有设置存储时按全开）。</summary>
    private FreightFeatureFlags Flags => _settings is null
        ? FreightFeatureFlags.All
        : FreightFeatureFlags.From(_settings.Current);

    /// <summary>新邮件入库后立即抽取（主题 + 摘要），不等待正文。</summary>
    public async Task IndexMessagesAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return;

        // 货代功能全关时直接跳过：不抽取、也不写扫描记录，
        // 这样重新开启后这些邮件仍会被补扫队列捡起来（存量数据保留，不做删除）。
        var flags = Flags;
        if (!flags.AnyRecognition) return;

        var entries = messages.Select(message =>
        {
            var body = message.BodyState == BodyState.Downloaded && !string.IsNullOrWhiteSpace(message.BodyText)
                ? message.BodyText
                : message.Preview;
            return BuildEntry(message.Id, message.DateUtc, message.Subject, body,
                message.BodyState == BodyState.Downloaded && !string.IsNullOrWhiteSpace(message.BodyText), flags);
        }).ToList();

        await PersistAsync(entries, ct).ConfigureAwait(false);
    }

    /// <summary>正文下载完成后重扫单封邮件，把只出现在正文里的箱号、封号与截关时间补进索引。</summary>
    public async Task IndexMessageAsync(EmailDetail detail, CancellationToken ct = default)
    {
        var flags = Flags;
        if (!flags.AnyRecognition) return;

        var hasBody = detail.BodyState == BodyState.Downloaded && !string.IsNullOrWhiteSpace(detail.BodyText);
        var entry = BuildEntry(detail.Id, detail.DateUtc, detail.Subject,
            hasBody ? detail.BodyText : detail.Preview, hasBody, flags);

        await PersistAsync(new[] { entry }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 后台补扫：每次处理 <paramref name="batchSize"/> 封，最多 <paramref name="maxBatches"/> 批。
    /// 返回本次处理的邮件数；已有补扫在运行时直接返回 0（单飞）。
    /// </summary>
    public async Task<int> IndexPendingAsync(
        int batchSize = 200,
        int maxBatches = 10,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _indexing, 1, 0) != 0) return 0;

        var processed = 0;
        try
        {
            var flags = Flags;
            if (!flags.AnyRecognition) return 0;

            for (var batch = 0; batch < maxBatches; batch++)
            {
                ct.ThrowIfCancellationRequested();

                var pending = await _repository.GetPendingScansAsync(batchSize, ct).ConfigureAwait(false);
                if (pending.Count == 0) break;

                var entries = pending.Select(row =>
                {
                    var isBody = row.HasBody && !string.IsNullOrWhiteSpace(row.BodyText);
                    return BuildEntry(row.EmailId, row.DateUtc, row.Subject,
                        isBody ? row.BodyText : row.Preview, isBody, flags);
                }).ToList();

                await PersistAsync(entries, ct).ConfigureAwait(false);
                processed += entries.Count;

                if (pending.Count < batchSize) break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "后台补扫业务标识失败（已处理 {Processed} 封）", processed);
        }
        finally
        {
            Volatile.Write(ref _indexing, 0);
        }

        return processed;
    }

    public Task<IReadOnlyList<FreightReferenceSummary>> GetTopAsync(
        long? accountId = null, int take = 12, CancellationToken ct = default)
        => _repository.GetTopAsync(accountId, take, ct);

    public Task<int> CountPendingAsync(CancellationToken ct = default) => _repository.CountPendingScansAsync(ct);

    /// <summary>
    /// 清空补扫进度：货代功能从「关闭」重新「开启」时调用，
    /// 让关闭期间入库的邮件重新排队抽取（已有记录不删除，只重建索引）。
    /// </summary>
    public Task ClearScanProgressAsync(CancellationToken ct = default) => _repository.ClearScanProgressAsync(ct);

    private static ScanEntry BuildEntry(
        long emailId,
        DateTimeOffset date,
        string? subject,
        string? body,
        bool bodyIndexed,
        FreightFeatureFlags flags)
    {
        // 按设置逐项抽取：关掉的项不写库，避免关掉后还在累积数据
        var references = flags.References
            ? FreightReferenceExtractor.Extract(subject, body)
            : Array.Empty<FreightReference>();
        // 年份推断以邮件自身日期为基准：历史邮件里的「3/15」不应被当成今年。
        var deadlines = flags.Deadlines
            ? FreightDeadlineExtractor.Extract(subject, body, date)
            : Array.Empty<FreightDeadline>();
        var type = flags.MailTypes
            ? MailTypeClassifier.Classify(subject, body, references.Count > 0)
            : MailTypeResult.Unknown;

        // 运输字段（箱型/航线/条款/金额）：纯规则，和单号同一批判定；开关关闭时不抽不写
        var fields = flags.AnyRecognition
            ? FreightShipmentFieldExtractor.Extract(subject, body)
            : ShipmentFields.Empty;

        return new ScanEntry(
            new ReferenceIndexEntry(emailId, bodyIndexed, references, type.Type, type.Score, type.Basis),
            new ShipmentIndexEntry
            {
                EmailId = emailId,
                MessageDate = date,
                References = references,
                Deadlines = deadlines,
                MailType = type.Type
            },
            fields);
    }

    private async Task PersistAsync(IReadOnlyList<ScanEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;

        try
        {
            await _repository.ReplaceRangeAsync(entries.Select(e => e.Reference).ToList(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "邮件入库时抽取业务标识失败（{Count} 封）", entries.Count);
            return; // 单号没落库时案件归并也一定对不上，直接跳过
        }

        if (_shipmentFields is not null)
        {
            try
            {
                await _shipmentFields
                    .ReplaceRangeAsync(entries.Select(e => new ShipmentFieldEntry(e.Reference.EmailId, e.Fields)).ToList(), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 运输字段只是展示与检索的增益，写失败不影响单号、类型与案件归并
                _logger.LogWarning(ex, "写入运输字段失败（{Count} 封）", entries.Count);
            }
        }

        try
        {
            await _shipments.ApplyAsync(entries.Select(e => e.Shipment).ToList(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 案件归并失败不应影响邮件入库与单号索引，下次补扫会重建。
            _logger.LogWarning(ex, "归并一票货案件失败（{Count} 封）", entries.Count);
        }

        // 会话继承：同一会话里已确定的类型补到仍未识别的邮件上（异常优先、其次多数）。
        // 只有本批真的判出了类型才需要跑，避免每批都扫一遍。
        if (entries.Any(entry => entry.Reference.MailType != MailType.Unknown))
        {
            try
            {
                await _repository.ApplyThreadInheritanceAsync(ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "会话类型继承失败（已索引 {Count} 封）", entries.Count);
            }
        }
    }

    private sealed record ScanEntry(ReferenceIndexEntry Reference, ShipmentIndexEntry Shipment, ShipmentFields Fields);
}
