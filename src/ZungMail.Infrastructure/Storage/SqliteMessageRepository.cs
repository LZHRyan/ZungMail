using System.Globalization;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 邮件仓储。列表查询一律分页（NFR-P4），全文搜索走 FTS5 并用 LIKE 兜底（FR-06），
/// 会话归并由 thread_id 完成（FR-05）。
/// </summary>
public sealed class SqliteMessageRepository : IMessageRepository
{
    private const string Cols =
        "m.id, m.account_id, m.folder_id, m.uid, m.message_id, m.thread_id, m.subject, m.from_name, m.from_email, " +
        "m.to_json, m.cc_json, m.bcc_json, m.reply_to, m.references_json, m.date_utc, m.preview, m.flags, m.size, " +
        "m.has_attachment, m.body_state, m.body_html, m.body_text, m.eml_path, m.mdn_to, m.mail_type, m.is_stranger, " +
        "m.is_bounce, m.bounce_recipient, m.bounce_status, m.bounce_detail";

    /// <summary>
    /// 列表 / 会话查询用的列清单：把正文两列换成空串常量。
    /// <see cref="MapSummary"/> 按列序号取值、不读正文，但 SQLite 仍会把 <c>body_html</c> /
    /// <c>body_text</c> 从溢出页读进记录缓冲——每页 100 封就是白读 100 份正文。
    /// 列数与顺序必须与 <see cref="Cols"/> 完全一致，否则序号会错位。
    /// </summary>
    private const string SummaryCols =
        "m.id, m.account_id, m.folder_id, m.uid, m.message_id, m.thread_id, m.subject, m.from_name, m.from_email, " +
        "m.to_json, m.cc_json, m.bcc_json, m.reply_to, m.references_json, m.date_utc, m.preview, m.flags, m.size, " +
        "m.has_attachment, m.body_state, '' AS body_html, '' AS body_text, m.eml_path, m.mdn_to, m.mail_type, m.is_stranger, " +
        "m.is_bounce, m.bounce_recipient, m.bounce_status, m.bounce_detail";

    // 会话分组键：迁移 v24 起是 messages 上的虚拟生成列（带索引），会话列表分组与回查都能走索引。
    private const string ThreadKey = "m.thread_key";

    private readonly SqliteConnectionFactory _factory;

    public SqliteMessageRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    // ---------------- 查询 ----------------

    public async Task<PagedResult<EmailSummary>> QueryAsync(MessageQuery query, CancellationToken ct = default)
    {
        // 搜索串先解析结构化运算符（booking: / bl: / ctr: / has:attachment …），剩余部分才是全文检索词
        var structured = MailSearchQuery.Parse(query.SearchText);
        if (structured.FreeText.Length > 0)
        {
            var matchExpr = FtsTextNormalizer.BuildMatchExpression(structured.FreeText);
            if (matchExpr.Length > 0)
            {
                var ftsResult = await RunQueryAsync(query, useFts: true, matchExpr, structured, ct).ConfigureAwait(false);
                if (ftsResult.TotalCount > 0) return ftsResult;
            }
            // 无有效 FTS 表达式（如纯符号）或 FTS 未命中时，回退 LIKE。
        }
        return await RunQueryAsync(query, useFts: false, null, structured, ct).ConfigureAwait(false);
    }

    private async Task<PagedResult<EmailSummary>> RunQueryAsync(
        MessageQuery query, bool useFts, string? matchExpr, MailSearchQuery structured, CancellationToken ct)
    {
        var (joins, where, prms) = BuildFilter(query, useFts, matchExpr, structured);
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        int total;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM messages m {joins} WHERE {where};";
            Apply(countCmd, prms);
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }

        var items = new List<EmailSummary>();
        if (total > 0)
        {
            await using var pageCmd = connection.CreateCommand();
            pageCmd.CommandText =
                $"SELECT {SummaryCols} FROM messages m {joins} WHERE {where} " +
                "ORDER BY m.date_utc DESC, m.id DESC LIMIT @take OFFSET @skip;";
            Apply(pageCmd, prms);
            pageCmd.Parameters.AddWithValue("@take", query.Take);
            pageCmd.Parameters.AddWithValue("@skip", Math.Max(0, query.Skip));
            await using var reader = await pageCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapSummary(reader));
            }
        }

        return new PagedResult<EmailSummary>
        {
            Items = items,
            TotalCount = total,
            HasMore = query.Skip + items.Count < total
        };
    }

    public async Task<PagedResult<ThreadGroup>> QueryThreadsAsync(MessageQuery query, CancellationToken ct = default)
    {
        var structured = MailSearchQuery.Parse(query.SearchText);
        if (structured.FreeText.Length > 0)
        {
            var matchExpr = FtsTextNormalizer.BuildMatchExpression(structured.FreeText);
            if (matchExpr.Length > 0)
            {
                var ftsResult = await RunThreadsAsync(query, useFts: true, matchExpr, structured, ct).ConfigureAwait(false);
                if (ftsResult.TotalCount > 0) return ftsResult;
            }
        }
        return await RunThreadsAsync(query, useFts: false, null, structured, ct).ConfigureAwait(false);
    }

    private async Task<PagedResult<ThreadGroup>> RunThreadsAsync(
        MessageQuery query, bool useFts, string? matchExpr, MailSearchQuery structured, CancellationToken ct)
    {
        var (joins, where, prms) = BuildFilter(query, useFts, matchExpr, structured);
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        int total;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText =
                $"SELECT COUNT(*) FROM (SELECT 1 FROM messages m {joins} WHERE {where} GROUP BY {ThreadKey});";
            Apply(countCmd, prms);
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }

        var groups = new List<ThreadGroup>();
        if (total > 0)
        {
            var keys = new List<string>();
            await using (var pageCmd = connection.CreateCommand())
            {
                pageCmd.CommandText =
                    $"SELECT {ThreadKey} AS tk, MAX(m.date_utc) AS maxdate FROM messages m {joins} WHERE {where} " +
                    "GROUP BY tk ORDER BY maxdate DESC LIMIT @take OFFSET @skip;";
                Apply(pageCmd, prms);
                pageCmd.Parameters.AddWithValue("@take", query.Take);
                pageCmd.Parameters.AddWithValue("@skip", Math.Max(0, query.Skip));
                await using var reader = await pageCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    keys.Add(reader.GetString(0));
                }
            }

            var itemsByKey = await LoadThreadItemsAsync(connection, keys, ct).ConfigureAwait(false);
            foreach (var key in keys)
            {
                if (!itemsByKey.TryGetValue(key, out var items) || items.Count == 0) continue;
                var representative = items[0]; // 查询按日期倒序，首封即最新
                items.Reverse();               // Items 按时间升序，便于阅读
                groups.Add(new ThreadGroup
                {
                    ThreadId = key,
                    Representative = representative,
                    Count = items.Count,
                    UnreadCount = items.Count(i => i.IsUnread),
                    HasAttachment = items.Any(i => i.HasAttachment),
                    Items = items
                });
            }
        }

        return new PagedResult<ThreadGroup>
        {
            Items = groups,
            TotalCount = total,
            HasMore = query.Skip + groups.Count < total
        };
    }

    /// <summary>一次 IN 查询取回整页所有会话的邮件（原来按每个会话各查一次，一页 100 行就是 100 次全表扫）。</summary>
    private static async Task<Dictionary<string, List<EmailSummary>>> LoadThreadItemsAsync(
        SqliteConnection connection, IReadOnlyList<string> threadKeys, CancellationToken ct)
    {
        var result = new Dictionary<string, List<EmailSummary>>(StringComparer.Ordinal);
        if (threadKeys.Count == 0) return result;

        var placeholders = string.Join(',', Enumerable.Range(0, threadKeys.Count).Select(i => "@tk" + i));
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SummaryCols}, m.thread_key AS tk FROM messages m " +
            $"WHERE m.thread_key IN ({placeholders}) ORDER BY m.date_utc DESC, m.id DESC;";
        for (var i = 0; i < threadKeys.Count; i++)
        {
            command.Parameters.AddWithValue("@tk" + i, threadKeys[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var keyIndex = reader.FieldCount - 1;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = SqlConvert.Str(reader, keyIndex);
            if (!result.TryGetValue(key, out var items))
            {
                items = new List<EmailSummary>();
                result[key] = items;
            }
            items.Add(MapSummary(reader));
        }
        return result;
    }

    public async Task<EmailDetail?> GetDetailAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        EmailDetail? detail;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {Cols} FROM messages m WHERE m.id = @id;";
            command.Parameters.AddWithValue("@id", id);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            detail = await reader.ReadAsync(ct).ConfigureAwait(false) ? MapDetail(reader) : null;
        }

        if (detail is null) return null;

        detail.Attachments = await LoadAttachmentsAsync(connection, id, ct).ConfigureAwait(false);
        return detail;
    }

    /// <summary>读取邮件附件（含内嵌图片；界面按 <see cref="AttachmentInfo.IsInline"/> 区分展示）。</summary>
    private static async Task<List<AttachmentInfo>> LoadAttachmentsAsync(
        SqliteConnection connection, long messageId, CancellationToken ct)
    {
        var result = new List<AttachmentInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT file_name, content_type, size, is_inline, local_path
FROM attachments
WHERE message_id = @id
ORDER BY id;
""";
        command.Parameters.AddWithValue("@id", messageId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new AttachmentInfo
            {
                EmailId = messageId,
                FileName = SqlConvert.Str(reader, 0),
                ContentType = SqlConvert.Str(reader, 1),
                Size = SqlConvert.Long(reader, 2),
                IsInline = SqlConvert.Bool(reader, 3),
                LocalPath = SqlConvert.StrOrNull(reader, 4)
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<EmailSummary>> GetThreadMessagesAsync(string threadId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SummaryCols} FROM messages m WHERE m.thread_id = @thread ORDER BY m.date_utc ASC, m.id ASC;";
        command.Parameters.AddWithValue("@thread", threadId);
        var items = new List<EmailSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(MapSummary(reader));
        }
        return items;
    }

    public async Task<EmailDetail?> FindByUidAsync(long folderId, uint uid, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Cols} FROM messages m WHERE m.folder_id = @folder AND m.uid = @uid;";
        command.Parameters.AddWithValue("@folder", folderId);
        command.Parameters.AddWithValue("@uid", (long)uid);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapDetail(reader) : null;
    }

    public async Task<EmailDetail?> FindByMessageIdAsync(
        long accountId,
        string messageId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return null;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Cols} FROM messages m WHERE m.account_id = @account AND m.message_id = @messageId LIMIT 1;";
        command.Parameters.AddWithValue("@account", accountId);
        command.Parameters.AddWithValue("@messageId", messageId.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapDetail(reader) : null;
    }

    // ---------------- 写入 ----------------

    public async Task<long> UpsertAsync(EmailDetail message, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var id = await UpsertCoreAsync(connection, null, message, ct).ConfigureAwait(false);
        message.Id = id;
        return id;
    }

    public async Task UpsertRangeAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return;
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var message in messages)
        {
            message.Id = await UpsertCoreAsync(connection, tx, message, ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 取给定地址中「已经认识」的那些（通讯录命中，或历史来信里出现过该发件人）。
    /// 必须在待判定的新邮件写库之前调用，否则本批写入的行会把同批发件人判成熟人。
    /// </summary>
    public async Task<IReadOnlySet<string>> GetKnownSenderAddressesAsync(
        IReadOnlyCollection<string> addresses, CancellationToken ct = default)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (addresses.Count == 0) return known;

        var normalized = new List<string>(addresses.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            var value = StrangerSenderPolicy.Normalize(address);
            if (value.Length == 0 || !seen.Add(value)) continue;
            normalized.Add(value);
        }

        if (normalized.Count == 0) return known;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        // SQLite 默认最多 999 个绑定变量：按 200 个一批，避免首轮全量同步时参数溢出
        foreach (var chunk in normalized.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();

            await using var command = connection.CreateCommand();
            var names = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var name = "@a" + i.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                command.Parameters.AddWithValue(name, chunk[i]);
            }

            var list = string.Join(", ", names);
            // from_email 的索引建在 from_email COLLATE NOCASE 上（v22），查询必须带上同样的 COLLATE 才命中；
            // contacts.email 本身声明为 NOCASE，直接 IN 即可走唯一索引。
            command.CommandText = $"""
SELECT LOWER(email) FROM contacts WHERE email IN ({list})
UNION
SELECT LOWER(from_email) FROM messages WHERE from_email COLLATE NOCASE IN ({list})
""";

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) continue;
                var value = reader.GetString(0);
                if (value.Length > 0) known.Add(value);
            }
        }

        return known;
    }

    private static async Task<long> UpsertCoreAsync(
        SqliteConnection connection, SqliteTransaction? tx, EmailDetail message, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        // 冲突更新：当库中正文已下载（body_state=1）而新记录未下载（body_state=0）时，保留库中正文（NFR-R1）。
        command.CommandText = """
INSERT INTO messages(account_id, folder_id, uid, message_id, thread_id, subject, from_name, from_email,
                     to_json, cc_json, bcc_json, reply_to, references_json, date_utc, preview, flags, size,
                     has_attachment, body_state, body_html, body_text, mdn_to, is_stranger,
                     is_bounce, bounce_recipient, bounce_status, bounce_detail,
                     fts_subject, fts_from, fts_to, fts_body)
VALUES(@accountId, @folderId, @uid, @messageId, @threadId, @subject, @fromName, @fromEmail,
       @to, @cc, @bcc, @replyTo, @references, @date, @preview, @flags, @size,
       @hasAttachment, @bodyState, @bodyHtml, @bodyText, @mdnTo, @isStranger,
       @isBounce, @bounceRecipient, @bounceStatus, @bounceDetail,
       @ftsSubject, @ftsFrom, @ftsTo, @ftsBody)
ON CONFLICT(folder_id, uid) DO UPDATE SET
    message_id       = excluded.message_id,
    thread_id        = COALESCE(NULLIF(excluded.thread_id, ''), messages.thread_id),
    subject          = excluded.subject,
    from_name        = excluded.from_name,
    from_email       = excluded.from_email,
    to_json          = excluded.to_json,
    cc_json          = excluded.cc_json,
    bcc_json         = excluded.bcc_json,
    reply_to         = excluded.reply_to,
    references_json  = excluded.references_json,
    mdn_to           = COALESCE(NULLIF(excluded.mdn_to, ''), messages.mdn_to),
    date_utc         = excluded.date_utc,
    preview          = excluded.preview,
    flags            = excluded.flags,
    size             = excluded.size,
    has_attachment   = excluded.has_attachment,
    body_state       = CASE WHEN messages.body_state = 1 AND excluded.body_state = 0 THEN messages.body_state ELSE excluded.body_state END,
    body_html        = CASE WHEN messages.body_state = 1 AND excluded.body_state = 0 THEN messages.body_html ELSE excluded.body_html END,
    body_text        = CASE WHEN messages.body_state = 1 AND excluded.body_state = 0 THEN messages.body_text ELSE excluded.body_text END,
    -- 陌生标识是「收信那一刻」的历史结论：已标记的邮件不会因为后续 upsert（补正文 / 重新拉摘要）被洗掉
    is_stranger      = CASE WHEN messages.is_stranger = 1 THEN 1 ELSE excluded.is_stranger END,
    -- 退信同理：判定结论与已解析的细节都保留（重新拉摘要不带细节时不能清空）
    is_bounce        = CASE WHEN messages.is_bounce = 1 THEN 1 ELSE excluded.is_bounce END,
    bounce_recipient = COALESCE(NULLIF(excluded.bounce_recipient, ''), messages.bounce_recipient),
    bounce_status    = COALESCE(NULLIF(excluded.bounce_status, ''), messages.bounce_status),
    bounce_detail    = COALESCE(NULLIF(excluded.bounce_detail, ''), messages.bounce_detail),
    fts_subject      = excluded.fts_subject,
    fts_from         = excluded.fts_from,
    fts_to           = excluded.fts_to,
    fts_body         = CASE WHEN messages.body_state = 1 AND excluded.body_state = 0 THEN messages.fts_body ELSE excluded.fts_body END;
SELECT id FROM messages WHERE folder_id = @folderId AND uid = @uid;
""";
        command.Parameters.AddWithValue("@accountId", message.AccountId);
        command.Parameters.AddWithValue("@folderId", message.FolderId);
        command.Parameters.AddWithValue("@uid", (long)message.Uid);
        command.Parameters.AddWithValue("@messageId", message.MessageId ?? string.Empty);
        command.Parameters.AddWithValue("@threadId", message.ThreadId ?? string.Empty);
        command.Parameters.AddWithValue("@subject", message.Subject ?? string.Empty);
        command.Parameters.AddWithValue("@fromName", message.FromName ?? string.Empty);
        command.Parameters.AddWithValue("@fromEmail", message.FromEmail ?? string.Empty);
        command.Parameters.AddWithValue("@to", JsonHelpers.SerializeAddresses(message.To));
        command.Parameters.AddWithValue("@cc", JsonHelpers.SerializeAddresses(message.Cc));
        command.Parameters.AddWithValue("@bcc", JsonHelpers.SerializeAddresses(message.Bcc));
        command.Parameters.AddWithValue("@replyTo", message.ReplyTo?.Address ?? string.Empty);
        command.Parameters.AddWithValue("@references", JsonHelpers.SerializeStrings(message.References));
        command.Parameters.AddWithValue("@date", SqlConvert.ToDb(message.DateUtc)!);
        command.Parameters.AddWithValue("@preview", message.Preview ?? string.Empty);
        command.Parameters.AddWithValue("@flags", (int)message.Flags);
        command.Parameters.AddWithValue("@size", message.Size);
        command.Parameters.AddWithValue("@hasAttachment", message.HasAttachment ? 1 : 0);
        command.Parameters.AddWithValue("@bodyState", (int)message.BodyState);
        command.Parameters.AddWithValue("@bodyHtml", (object?)NullIfEmpty(message.BodyHtml) ?? DBNull.Value);
        command.Parameters.AddWithValue("@bodyText", (object?)NullIfEmpty(message.BodyText) ?? DBNull.Value);
        command.Parameters.AddWithValue("@ftsSubject", FtsTextNormalizer.Normalize(message.Subject));
        command.Parameters.AddWithValue("@ftsFrom", NormalizeFrom(message));
        command.Parameters.AddWithValue("@ftsTo", NormalizeTo(message));
        command.Parameters.AddWithValue("@mdnTo", (object?)NullIfEmpty(message.MdnTo) ?? DBNull.Value);
        command.Parameters.AddWithValue("@isStranger", message.IsStranger ? 1 : 0);
        command.Parameters.AddWithValue("@isBounce", message.IsBounce ? 1 : 0);
        command.Parameters.AddWithValue("@bounceRecipient", message.BounceRecipient ?? string.Empty);
        command.Parameters.AddWithValue("@bounceStatus", message.BounceStatus ?? string.Empty);
        command.Parameters.AddWithValue("@bounceDetail", message.BounceDetail ?? string.Empty);
        command.Parameters.AddWithValue("@ftsBody", NormalizeBody(message));

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);

        // thread_id 为空的邮件写回 local:<行 id>，使其自成一会话。
        if (string.IsNullOrEmpty(message.ThreadId))
        {
            await using var fix = connection.CreateCommand();
            fix.Transaction = tx;
            fix.CommandText = "UPDATE messages SET thread_id = 'local:' || id WHERE id = @id AND (thread_id IS NULL OR thread_id = '');";
            fix.Parameters.AddWithValue("@id", id);
            await fix.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return id;
    }

    public async Task UpdateFlagsAsync(long id, MessageFlags flags, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET flags = @flags WHERE id = @id;";
        command.Parameters.AddWithValue("@flags", (int)flags);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateFlagsRangeAsync(IReadOnlyList<(long Id, MessageFlags Flags)> updates, CancellationToken ct = default)
    {
        if (updates.Count == 0) return;
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var (id, flags) in updates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "UPDATE messages SET flags = @flags WHERE id = @id;";
            command.Parameters.AddWithValue("@flags", (int)flags);
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkDeletedAsync(IReadOnlyList<long> messageIds, CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        foreach (var chunk in messageIds.Distinct().Chunk(400))
        {
            await using var command = connection.CreateCommand();
            var names = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                names[i] = "@id" + i;
                command.Parameters.AddWithValue(names[i], chunk[i]);
            }
            command.CommandText =
                $"UPDATE messages SET flags = flags | {(int)MessageFlags.Deleted} WHERE id IN ({string.Join(',', names)});";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<(long Id, long FolderId, MessageFlags Flags)>> MarkAllSeenAsync(
        MessageQuery query, CancellationToken ct = default)
    {
        // 搜索条件用 LIKE 兜底，避免为 UPDATE 再拼一套 FTS 回退逻辑
        var (joins, where, prms) = BuildFilter(query, useFts: false, matchExpr: null, MailSearchQuery.Parse(query.SearchText));
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var targets = new List<(long Id, long FolderId, MessageFlags Flags)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                $"SELECT m.id, m.folder_id, m.flags FROM messages m {joins} " +
                $"WHERE {where} AND (m.flags & 1) = 0;";
            Apply(select, prms);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                targets.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    (MessageFlags)SqlConvert.Int(reader, 2) | MessageFlags.Seen));
            }
        }

        if (targets.Count == 0) return targets;

        // 分批 UPDATE，避免 SQLite 单语句参数上限
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var chunk in targets.Chunk(400))
        {
            await using var update = connection.CreateCommand();
            update.Transaction = tx;
            var names = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                names[i] = "@id" + i;
                update.Parameters.AddWithValue(names[i], chunk[i].Id);
            }
            update.CommandText =
                $"UPDATE messages SET flags = flags | {(int)MessageFlags.Seen} WHERE id IN ({string.Join(',', names)});";
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return targets;
    }

    public async Task<IReadOnlyList<(long Id, uint Uid, MessageFlags Flags)>> GetFlagsAsync(long folderId, uint minUidExclusive, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, uid, flags FROM messages WHERE folder_id = @folder AND uid > @minUid AND (flags & 32) = 0;";
        command.Parameters.AddWithValue("@folder", folderId);
        command.Parameters.AddWithValue("@minUid", (long)minUidExclusive);
        var result = new List<(long, uint, MessageFlags)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add((reader.GetInt64(0), (uint)reader.GetInt64(1), (MessageFlags)SqlConvert.Int(reader, 2)));
        }
        return result;
    }

    public async Task<uint> GetMaxUidAsync(long folderId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(uid), 0) FROM messages WHERE folder_id = @folder;";
        command.Parameters.AddWithValue("@folder", folderId);
        return (uint)Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    /// <summary>
    /// 正文预取窗口内仍缺正文的邮件：(folder_id, date_utc DESC) 索引可直接覆盖，
    /// body_state 只作为窗口内的残余过滤。
    /// </summary>
    public async Task<IReadOnlyList<(long Id, uint Uid)>> GetPendingBodyDownloadsAsync(
        long folderId, DateTimeOffset? sinceUtc, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return Array.Empty<(long, uint)>();

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sinceUtc is null
            ? "SELECT id, uid FROM messages WHERE folder_id = @folder AND body_state = 0 " +
              "ORDER BY date_utc DESC, id DESC LIMIT @limit;"
            : "SELECT id, uid FROM messages WHERE folder_id = @folder AND body_state = 0 AND date_utc >= @since " +
              "ORDER BY date_utc DESC, id DESC LIMIT @limit;";
        command.Parameters.AddWithValue("@folder", folderId);
        command.Parameters.AddWithValue("@limit", limit);
        if (sinceUtc is { } since) command.Parameters.AddWithValue("@since", SqlConvert.ToDb(since));

        var result = new List<(long, uint)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add((reader.GetInt64(0), (uint)reader.GetInt64(1)));
        }
        return result;
    }

    /// <summary>彻底删除一封邮件：先收集本地文件路径，删 attachments / messages 行，再删磁盘文件。</summary>
    public async Task<int> DeletePermanentlyAsync(long id, CancellationToken ct = default)
    {
        var files = new List<string>();

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var query = connection.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT eml_path FROM messages WHERE id = @id;";
            query.Parameters.AddWithValue("@id", id);
            if (await query.ExecuteScalarAsync(ct).ConfigureAwait(false) is string eml && eml.Length > 0) files.Add(eml);
        }

        await using (var query = connection.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT local_path FROM attachments WHERE message_id = @id AND local_path IS NOT NULL;";
            query.Parameters.AddWithValue("@id", id);
            await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0)) files.Add(reader.GetString(0));
            }
        }

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM attachments WHERE message_id = @id;";
            clear.Parameters.AddWithValue("@id", id);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM messages WHERE id = @id;";
            clear.Parameters.AddWithValue("@id", id);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var removed = 0;
        foreach (var file in files.Distinct(System.StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (System.IO.File.Exists(file))
                {
                    System.IO.File.Delete(file);
                    removed++;
                }
            }
            catch (System.IO.IOException)
            {
                // 文件被占用：库记录已删，残留文件交给空间清理回收
            }
            catch (System.UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    public async Task UpdateBounceAsync(
        long id, bool isBounce, string recipient, string status, string detail, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 空串表示「本次没解析出来」：保留库中原值，避免重新下载正文把已有细节洗掉
        command.CommandText = """
UPDATE messages SET
    is_bounce        = CASE WHEN @isBounce = 1 THEN 1 ELSE is_bounce END,
    bounce_recipient = COALESCE(NULLIF(@recipient, ''), bounce_recipient),
    bounce_status    = COALESCE(NULLIF(@status, ''), bounce_status),
    bounce_detail    = COALESCE(NULLIF(@detail, ''), bounce_detail)
WHERE id = @id;
""";
        command.Parameters.AddWithValue("@isBounce", isBounce ? 1 : 0);
        command.Parameters.AddWithValue("@recipient", recipient ?? string.Empty);
        command.Parameters.AddWithValue("@status", status ?? string.Empty);
        command.Parameters.AddWithValue("@detail", detail ?? string.Empty);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveBodyAsync(long id, string bodyHtml, string bodyText, IReadOnlyList<AttachmentInfo> attachments, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
UPDATE messages SET body_html = @html, body_text = @text, body_state = 1, fts_body = @ftsBody WHERE id = @id;
""";
            update.Parameters.AddWithValue("@html", (object?)NullIfEmpty(bodyHtml) ?? DBNull.Value);
            update.Parameters.AddWithValue("@text", (object?)NullIfEmpty(bodyText) ?? DBNull.Value);
            // 附件名一并进入正文索引列，使「按附件名搜索」与正文共用同一套 FTS（需求 1）
            var indexText = (string.IsNullOrWhiteSpace(bodyText) ? HtmlTextExtractor.ToPlainText(bodyHtml) : bodyText)
                + " "
                + string.Join(' ', attachments.Where(a => !a.IsInline).Select(a => a.FileName));
            update.Parameters.AddWithValue("@ftsBody", FtsTextNormalizer.Normalize(indexText));
            update.Parameters.AddWithValue("@id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM attachments WHERE message_id = @id;";
            clear.Parameters.AddWithValue("@id", id);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var attachment in attachments)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
INSERT INTO attachments(message_id, file_name, content_type, size, is_inline, local_path)
VALUES(@messageId, @fileName, @contentType, @size, @inline, @localPath);
""";
            insert.Parameters.AddWithValue("@messageId", id);
            insert.Parameters.AddWithValue("@fileName", attachment.FileName ?? string.Empty);
            insert.Parameters.AddWithValue("@contentType", attachment.ContentType ?? "application/octet-stream");
            insert.Parameters.AddWithValue("@size", attachment.Size);
            insert.Parameters.AddWithValue("@inline", attachment.IsInline ? 1 : 0);
            insert.Parameters.AddWithValue("@localPath", (object?)attachment.LocalPath ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<long>> GetInlineDataImageMessageIdsAsync(int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return Array.Empty<long>();

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 一次性回填用：LIKE 走不了索引，全表扫一遍，只在 meta 里没标记时调用一次
        command.CommandText = """
SELECT id FROM messages
WHERE body_state = 1 AND body_html LIKE '%data:image/%base64,%'
ORDER BY id
LIMIT @limit;
""";
        command.Parameters.AddWithValue("@limit", limit);

        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    public async Task DeleteByFolderAsync(long folderId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE folder_id = @folder;";
        command.Parameters.AddWithValue("@folder", folderId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> ClearBodyCacheAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        int affected;
        await using (var update = connection.CreateCommand())
        {
            // 清空已下载正文；附件文件的物理删除交给调用方，这里仅清理非 inline 附件的库记录。
            update.Transaction = tx;
            update.CommandText = "UPDATE messages SET body_html = NULL, body_text = NULL, body_state = 0, fts_body = '' WHERE body_state = 1;";
            affected = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM attachments WHERE is_inline = 0;";
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return affected;
    }

    public async Task<int> CountAsync(long? accountId = null, long? folderId = null, CancellationToken ct = default)
    {
        var (tail, prms) = BuildCountFilter(accountId, folderId, unreadOnly: false);
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM messages m {tail};";
        Apply(command, prms);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    public async Task<int> CountUnreadAsync(long? accountId = null, long? folderId = null, CancellationToken ct = default)
    {
        var (tail, prms) = BuildCountFilter(accountId, folderId, unreadOnly: true);
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM messages m {tail};";
        Apply(command, prms);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    public async Task<IReadOnlyDictionary<long, (int Total, int Unread)>> GetFolderCountsAsync(
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, (int Total, int Unread)>();

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 与列表同一口径：本地已删除（\Deleted，32）的不计入，所以「0 封」的文件夹也不会挂未读角标
        command.CommandText = """
SELECT folder_id, COUNT(*), COALESCE(SUM(CASE WHEN (flags & 1) = 0 THEN 1 ELSE 0 END), 0)
FROM messages
WHERE (flags & 32) = 0
GROUP BY folder_id;
""";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetInt64(0)] = (Convert.ToInt32(reader.GetInt64(1)), Convert.ToInt32(reader.GetInt64(2)));
        }

        return result;
    }

    public async Task<long> GetStorageSizeAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // size 是服务器返回的整封邮件字节数（含正文与附件），直接求和即可，不必再读正文。
        // 旧写法额外加 length(body_html)+length(body_text)：既要扫全表正文（1.1 万封实测 304ms，
        // 慢机器上就是切 Tab 卡顿的元凶），又把正文重复算了一遍。只有 size 缺失（0，通常是
        // 本地导入或服务器没给）的行才回退用正文长度估算，CASE 保证 length() 只对这几行求值。
        command.CommandText = """
SELECT COALESCE(SUM(size), 0)
       + COALESCE(SUM(CASE WHEN size <= 0
                           THEN length(COALESCE(body_html, '')) + length(COALESCE(body_text, ''))
                           ELSE 0 END), 0)
FROM messages;
""";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    // ---------------- 条件拼装 ----------------

    private static (string Joins, string Where, List<(string Name, object? Value)> Params) BuildFilter(
        MessageQuery query, bool useFts, string? matchExpr, MailSearchQuery structured)
    {
        var joins = "JOIN folders f ON f.id = m.folder_id";
        var conditions = new List<string>();
        var prms = new List<(string, object?)>();

        // 本地已删除但服务器尚未确认的邮件不再出现在列表 / 搜索结果中（32 = \Deleted）
        conditions.Add("(m.flags & 32) = 0");

        if (query.AccountId.HasValue)
        {
            conditions.Add("m.account_id = @accountId");
            prms.Add(("@accountId", query.AccountId.Value));
        }
        if (query.FolderId.HasValue)
        {
            conditions.Add("m.folder_id = @folderId");
            prms.Add(("@folderId", query.FolderId.Value));
        }
        if (query.EmailId.HasValue)
        {
            conditions.Add("m.id = @emailId");
            prms.Add(("@emailId", query.EmailId.Value));
        }
        if (query.MailType is { } mailType)
        {
            conditions.Add("m.mail_type = @mailType");
            prms.Add(("@mailType", (int)mailType));
        }
        if (structured.MailType is { } structuredType)
        {
            conditions.Add("m.mail_type = @structuredType");
            prms.Add(("@structuredType", (int)structuredType));
        }        if (structured.After is { } after)
        {
            conditions.Add("m.date_utc >= @after");
            prms.Add(("@after", SqlConvert.ToDb(after)!));
        }
        if (structured.Before is { } before)
        {
            conditions.Add("m.date_utc < @before");
            prms.Add(("@before", SqlConvert.ToDb(before)!));
        }
        if (query.FolderKind.HasValue)
        {
            conditions.Add("f.kind = @folderKind");
            prms.Add(("@folderKind", (int)query.FolderKind.Value));
        }
        if (query.TagId.HasValue)
        {
            // 标签是多对多关联，用 EXISTS 避免 JOIN 造成重复行并影响计数。
            conditions.Add("EXISTS (SELECT 1 FROM message_tags mt WHERE mt.email_id = m.id AND mt.tag_id = @tagId)");
            prms.Add(("@tagId", query.TagId.Value));
        }
        if (query.ExcludeTrash)
        {
            conditions.Add("f.kind <> 4"); // 4 = Trash
        }

        switch (query.Filter)
        {
            case MailListFilter.Unread:
                conditions.Add("(m.flags & 1) = 0");
                break;
            case MailListFilter.Starred:
                conditions.Add("(m.flags & 4) <> 0");
                break;
            case MailListFilter.Attachments:
                conditions.Add("m.has_attachment = 1");
                break;
        }

        var search = structured.FreeText;
        if (search.Length > 0)
        {
            if (useFts && !string.IsNullOrEmpty(matchExpr))
            {
                joins += " JOIN messages_fts ON messages_fts.email_id = m.id";
                conditions.Add("messages_fts MATCH @ftsMatch");
                prms.Add(("@ftsMatch", matchExpr));
            }
            else
            {
                // LIKE 兜底覆盖：主题、发件人、收件人/抄送/密送、正文、附件名（与 FTS 维度保持一致）
                conditions.Add(
                    "(m.subject LIKE @like OR m.from_name LIKE @like OR m.from_email LIKE @like " +
                    "OR m.to_json LIKE @like OR m.cc_json LIKE @like OR m.bcc_json LIKE @like " +
                    "OR m.preview LIKE @like OR m.body_text LIKE @like " +
                    "OR EXISTS (SELECT 1 FROM attachments a WHERE a.message_id = m.id AND a.file_name LIKE @like))");
                prms.Add(("@like", FtsTextNormalizer.ToLikePattern(search)));
            }
        }

        // 货代业务标识过滤：booking: / bl: / ctr: / vessel: / ref: …
        // 用 EXISTS 避免 JOIN 造成重复行；匹配规范化值，允许部分输入（SO2026 命中 SO-2026-001）
        for (var i = 0; i < structured.References.Count; i++)
        {
            var reference = structured.References[i];
            var valueParam = "@refValue" + i;
            if (reference.Kind is { } kind)
            {
                var kindParam = "@refKind" + i;
                conditions.Add(
                    $"EXISTS (SELECT 1 FROM message_refs r WHERE r.email_id = m.id AND r.kind = {kindParam} AND r.normalized LIKE {valueParam})");
                prms.Add((kindParam, (int)kind));
            }
            else
            {
                conditions.Add(
                    $"EXISTS (SELECT 1 FROM message_refs r WHERE r.email_id = m.id AND r.normalized LIKE {valueParam})");
            }
            prms.Add((valueParam, "%" + reference.Normalized + "%"));
        }

        if (!string.IsNullOrWhiteSpace(structured.From))
        {
            conditions.Add("(m.from_email LIKE @fromLike OR m.from_name LIKE @fromLike)");
            prms.Add(("@fromLike", FtsTextNormalizer.ToLikePattern(structured.From)));
        }
        if (!string.IsNullOrWhiteSpace(structured.Subject))
        {
            conditions.Add("m.subject LIKE @subjectLike");
            prms.Add(("@subjectLike", FtsTextNormalizer.ToLikePattern(structured.Subject)));
        }
        if (!string.IsNullOrWhiteSpace(structured.FileName))
        {
            conditions.Add("EXISTS (SELECT 1 FROM attachments a WHERE a.message_id = m.id AND a.file_name LIKE @fileLike)");
            prms.Add(("@fileLike", FtsTextNormalizer.ToLikePattern(structured.FileName)));
        }
        if (structured.HasAttachment) conditions.Add("m.has_attachment = 1");
        if (structured.IsUnread is { } unread) conditions.Add(unread ? "(m.flags & 1) = 0" : "(m.flags & 1) <> 0");
        if (structured.IsStarred is true) conditions.Add("(m.flags & 4) <> 0");

        ApplyStructuredFilter(query, conditions, prms);

        var where = conditions.Count == 0 ? "1 = 1" : string.Join(" AND ", conditions);
        return (joins, where, prms);
    }

    /// <summary>
    /// 「全部搜索条件」面板的结构化条件（发件人 / 收件人 / 主题 / 全文 / 附件 / 标签 / 提醒 / 备注 / 时间），
    /// 与手写语法、FTS 关键词是「与」关系。
    /// </summary>
    private static void ApplyStructuredFilter(
        MessageQuery query, List<string> conditions, List<(string Name, object? Value)> prms)
    {
        if (!string.IsNullOrWhiteSpace(query.FromText))
        {
            conditions.Add("(m.from_email LIKE @fFrom OR m.from_name LIKE @fFrom)");
            prms.Add(("@fFrom", FtsTextNormalizer.ToLikePattern(query.FromText)));
        }

        if (!string.IsNullOrWhiteSpace(query.ToText))
        {
            // 收件人一栏在 Foxmail 里同时覆盖收件人 / 抄送 / 密送（地址都存在 JSON 里）
            conditions.Add("(m.to_json LIKE @fTo OR m.cc_json LIKE @fTo OR m.bcc_json LIKE @fTo)");
            prms.Add(("@fTo", FtsTextNormalizer.ToLikePattern(query.ToText)));
        }

        if (!string.IsNullOrWhiteSpace(query.SubjectText))
        {
            conditions.Add("m.subject LIKE @fSubject");
            prms.Add(("@fSubject", FtsTextNormalizer.ToLikePattern(query.SubjectText)));
        }

        if (!string.IsNullOrWhiteSpace(query.BodyText))
        {
            conditions.Add("(m.body_text LIKE @fBody OR m.body_html LIKE @fBody)");
            prms.Add(("@fBody", FtsTextNormalizer.ToLikePattern(query.BodyText)));
        }

        if (!string.IsNullOrWhiteSpace(query.FileNameText))
        {
            conditions.Add("EXISTS (SELECT 1 FROM attachments a WHERE a.message_id = m.id AND a.file_name LIKE @fFileName)");
            prms.Add(("@fFileName", FtsTextNormalizer.ToLikePattern(query.FileNameText)));
        }

        if (query.HasAttachment is { } hasAttachment)
        {
            conditions.Add(hasAttachment ? "m.has_attachment = 1" : "m.has_attachment = 0");
        }

        if (query.TagId is null && query.HasAnyTag is { } hasTag)
        {
            conditions.Add(hasTag
                ? "EXISTS (SELECT 1 FROM message_tags mt WHERE mt.email_id = m.id)"
                : "NOT EXISTS (SELECT 1 FROM message_tags mt WHERE mt.email_id = m.id)");
        }

        if (query.HasReminder is { } hasReminder)
        {
            // 提醒 = 进行中的「等待回复」跟进，或未完成的待办
            const string anyReminder =
                "(EXISTS (SELECT 1 FROM mail_followups f WHERE f.email_id = m.id AND f.state = 0) " +
                "OR EXISTS (SELECT 1 FROM mail_todos t WHERE t.email_id = m.id AND t.state = 0))";
            conditions.Add(hasReminder ? anyReminder : "NOT " + anyReminder);
        }

        if (!string.IsNullOrWhiteSpace(query.NoteText))
        {
            conditions.Add("EXISTS (SELECT 1 FROM message_notes n WHERE n.email_id = m.id AND n.note LIKE @fNote)");
            prms.Add(("@fNote", FtsTextNormalizer.ToLikePattern(query.NoteText)));
        }

        if (query.SinceUtc is { } since)
        {
            conditions.Add("m.date_utc >= @fSince");
            prms.Add(("@fSince", SqlConvert.ToDb(since)!));
        }

        if (query.BeforeUtc is { } before)
        {
            conditions.Add("m.date_utc < @fBefore");
            prms.Add(("@fBefore", SqlConvert.ToDb(before)!));
        }
    }

    private static (string Tail, List<(string Name, object? Value)> Params) BuildCountFilter(
        long? accountId, long? folderId, bool unreadOnly)
    {
        var conditions = new List<string>();
        var prms = new List<(string, object?)>();
        // 与列表口径一致：本地已删除的邮件不计入总数 / 未读数
        conditions.Add("(m.flags & 32) = 0");
        if (accountId.HasValue)
        {
            conditions.Add("m.account_id = @accountId");
            prms.Add(("@accountId", accountId.Value));
        }
        if (folderId.HasValue)
        {
            conditions.Add("m.folder_id = @folderId");
            prms.Add(("@folderId", folderId.Value));
        }
        if (unreadOnly)
        {
            conditions.Add("(m.flags & 1) = 0");
        }
        var tail = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
        return (tail, prms);
    }

    private static void Apply(SqliteCommand command, List<(string Name, object? Value)> prms)
    {
        foreach (var (name, value) in prms)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    // ---------------- 映射 ----------------

    private static EmailSummary MapSummary(SqliteDataReader reader) => new()
    {
        Id = SqlConvert.Long(reader, 0),
        AccountId = SqlConvert.Long(reader, 1),
        FolderId = SqlConvert.Long(reader, 2),
        Uid = (uint)SqlConvert.Long(reader, 3),
        MessageId = SqlConvert.Str(reader, 4),
        ThreadId = SqlConvert.Str(reader, 5),
        Subject = SqlConvert.Str(reader, 6),
        FromName = SqlConvert.Str(reader, 7),
        FromEmail = SqlConvert.Str(reader, 8),
        DateUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 14)),
        Preview = SqlConvert.Str(reader, 15),
        Flags = (MessageFlags)SqlConvert.Int(reader, 16),
        Size = SqlConvert.Long(reader, 17),
        HasAttachment = SqlConvert.Bool(reader, 18),
        IndustryType = (MailType)SqlConvert.Int(reader, 24),
        IsStranger = SqlConvert.Bool(reader, 25),
        IsBounce = SqlConvert.Bool(reader, 26),
        BounceRecipient = SqlConvert.Str(reader, 27),
        BounceStatus = SqlConvert.Str(reader, 28),
        BounceDetail = SqlConvert.Str(reader, 29)
    };

    private static EmailDetail MapDetail(SqliteDataReader reader)
    {
        var detail = new EmailDetail
        {
            Id = SqlConvert.Long(reader, 0),
            AccountId = SqlConvert.Long(reader, 1),
            FolderId = SqlConvert.Long(reader, 2),
            Uid = (uint)SqlConvert.Long(reader, 3),
            MessageId = SqlConvert.Str(reader, 4),
            ThreadId = SqlConvert.Str(reader, 5),
            Subject = SqlConvert.Str(reader, 6),
            FromName = SqlConvert.Str(reader, 7),
            FromEmail = SqlConvert.Str(reader, 8),
            To = JsonHelpers.DeserializeAddresses(SqlConvert.StrOrNull(reader, 9)),
            Cc = JsonHelpers.DeserializeAddresses(SqlConvert.StrOrNull(reader, 10)),
            Bcc = JsonHelpers.DeserializeAddresses(SqlConvert.StrOrNull(reader, 11)),
            References = JsonHelpers.DeserializeStrings(SqlConvert.StrOrNull(reader, 13)),
            DateUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 14)),
            Preview = SqlConvert.Str(reader, 15),
            Flags = (MessageFlags)SqlConvert.Int(reader, 16),
            Size = SqlConvert.Long(reader, 17),
            HasAttachment = SqlConvert.Bool(reader, 18),
            BodyState = (BodyState)SqlConvert.Int(reader, 19),
            BodyHtml = SqlConvert.Str(reader, 20),
            BodyText = SqlConvert.Str(reader, 21),
            MdnTo = SqlConvert.StrOrNull(reader, 22) ?? "",
            IndustryType = (MailType)SqlConvert.Int(reader, 24),
            IsStranger = SqlConvert.Bool(reader, 25),
            IsBounce = SqlConvert.Bool(reader, 26),
            BounceRecipient = SqlConvert.Str(reader, 27),
            BounceStatus = SqlConvert.Str(reader, 28),
            BounceDetail = SqlConvert.Str(reader, 29)
        };

        var replyTo = SqlConvert.Str(reader, 12);
        if (!string.IsNullOrEmpty(replyTo))
        {
            detail.ReplyTo = new EmailAddress(replyTo);
        }
        return detail;
    }

    public async Task<int> RebuildSearchIndexAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var processed = 0;
        long lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var rows = new List<(long Id, string Subject, string From, string To, string Body)>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = """
SELECT m.id, m.subject, m.from_name, m.from_email, m.to_json, m.cc_json, m.bcc_json,
       COALESCE(m.body_text, ''), COALESCE(m.body_html, ''),
       COALESCE((SELECT group_concat(a.file_name, ' ')
                 FROM attachments a
                 WHERE a.message_id = m.id AND a.is_inline = 0), '')
FROM messages m
WHERE m.id > @lastId
ORDER BY m.id
LIMIT 500;
""";
                select.Parameters.AddWithValue("@lastId", lastId);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var bodyText = SqlConvert.Str(reader, 7);
                    var bodyHtml = SqlConvert.Str(reader, 8);
                    var attachmentNames = SqlConvert.Str(reader, 9);
                    var body = (string.IsNullOrWhiteSpace(bodyText)
                            ? HtmlTextExtractor.ToPlainText(bodyHtml)
                            : bodyText)
                        + " " + attachmentNames;

                    rows.Add((
                        SqlConvert.Long(reader, 0),
                        FtsTextNormalizer.Normalize(SqlConvert.Str(reader, 1)),
                        FtsTextNormalizer.Normalize(
                            $"{SqlConvert.Str(reader, 2)} {SqlConvert.Str(reader, 3)}"),
                        NormalizeAddresses(SqlConvert.StrOrNull(reader, 4))
                            + " " + NormalizeAddresses(SqlConvert.StrOrNull(reader, 5))
                            + " " + NormalizeAddresses(SqlConvert.StrOrNull(reader, 6)),
                        FtsTextNormalizer.Normalize(body)));
                }
            }

            if (rows.Count == 0) break;

            await using (var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                foreach (var row in rows)
                {
                    await using var update = connection.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = """
UPDATE messages
SET fts_subject = @subject, fts_from = @from, fts_to = @to, fts_body = @body
WHERE id = @id;
""";
                    update.Parameters.AddWithValue("@subject", row.Subject);
                    update.Parameters.AddWithValue("@from", row.From);
                    update.Parameters.AddWithValue("@to", row.To.Trim());
                    update.Parameters.AddWithValue("@body", row.Body);
                    update.Parameters.AddWithValue("@id", row.Id);
                    await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            lastId = rows[^1].Id;
            processed += rows.Count;
            progress?.Report(processed);
        }

        // 合并 FTS 段，降低后续查询延迟
        await using (var optimize = connection.CreateCommand())
        {
            optimize.CommandText = "INSERT INTO messages_fts(messages_fts) VALUES('optimize');";
            await optimize.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return processed;
    }

    public async Task<bool> RestoreAsync(EmailDetail message, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = tx;
            exists.CommandText = "SELECT COUNT(*) FROM messages WHERE id = @id;";
            exists.Parameters.AddWithValue("@id", message.Id);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) > 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
        }

        // 撤销清理时按原 id 恢复，保证附件目录（accountId/messageId）与审计记录一致
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
INSERT INTO messages(id, account_id, folder_id, uid, message_id, thread_id, subject, from_name, from_email,
                     to_json, cc_json, bcc_json, reply_to, references_json, date_utc, preview, flags, size,
                     has_attachment, body_state, body_html, body_text, mdn_to,
                     fts_subject, fts_from, fts_to, fts_body)
VALUES(@id, @accountId, @folderId, @uid, @messageId, @threadId, @subject, @fromName, @fromEmail,
       @to, @cc, @bcc, @replyTo, @references, @date, @preview, @flags, @size,
       @hasAttachment, @bodyState, @bodyHtml, @bodyText, @mdnTo,
       @ftsSubject, @ftsFrom, @ftsTo, @ftsBody);
""";
            insert.Parameters.AddWithValue("@id", message.Id);
            insert.Parameters.AddWithValue("@accountId", message.AccountId);
            insert.Parameters.AddWithValue("@folderId", message.FolderId);
            insert.Parameters.AddWithValue("@uid", (long)message.Uid);
            insert.Parameters.AddWithValue("@messageId", message.MessageId ?? string.Empty);
            insert.Parameters.AddWithValue("@threadId", message.ThreadId ?? string.Empty);
            insert.Parameters.AddWithValue("@subject", message.Subject ?? string.Empty);
            insert.Parameters.AddWithValue("@fromName", message.FromName ?? string.Empty);
            insert.Parameters.AddWithValue("@fromEmail", message.FromEmail ?? string.Empty);
            insert.Parameters.AddWithValue("@to", JsonHelpers.SerializeAddresses(message.To));
            insert.Parameters.AddWithValue("@cc", JsonHelpers.SerializeAddresses(message.Cc));
            insert.Parameters.AddWithValue("@bcc", JsonHelpers.SerializeAddresses(message.Bcc));
            insert.Parameters.AddWithValue("@replyTo", message.ReplyTo?.Address ?? string.Empty);
            insert.Parameters.AddWithValue("@references", JsonHelpers.SerializeStrings(message.References));
            insert.Parameters.AddWithValue("@date", SqlConvert.ToDb(message.DateUtc)!);
            insert.Parameters.AddWithValue("@preview", message.Preview ?? string.Empty);
            insert.Parameters.AddWithValue("@flags", (int)message.Flags);
            insert.Parameters.AddWithValue("@size", message.Size);
            insert.Parameters.AddWithValue("@hasAttachment", message.HasAttachment ? 1 : 0);
            insert.Parameters.AddWithValue("@bodyState", (int)message.BodyState);
            insert.Parameters.AddWithValue("@bodyHtml", (object?)NullIfEmpty(message.BodyHtml) ?? DBNull.Value);
            insert.Parameters.AddWithValue("@bodyText", (object?)NullIfEmpty(message.BodyText) ?? DBNull.Value);
            insert.Parameters.AddWithValue("@ftsSubject", FtsTextNormalizer.Normalize(message.Subject));
            insert.Parameters.AddWithValue("@ftsFrom", NormalizeFrom(message));
            insert.Parameters.AddWithValue("@ftsTo", NormalizeTo(message));
            insert.Parameters.AddWithValue("@mdnTo", (object?)NullIfEmpty(message.MdnTo) ?? DBNull.Value);
        insert.Parameters.AddWithValue("@ftsBody", FtsTextNormalizer.Normalize(
                (string.IsNullOrWhiteSpace(message.BodyText)
                    ? HtmlTextExtractor.ToPlainText(message.BodyHtml)
                    : message.BodyText)
                + " " + string.Join(' ', message.Attachments
                    .Where(a => !a.IsInline)
                    .Select(a => a.FileName))));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var attachment in message.Attachments.Where(a => !a.IsInline))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
INSERT INTO attachments(message_id, file_name, content_type, size, is_inline, local_path)
VALUES(@messageId, @fileName, @contentType, @size, 0, @localPath);
""";
            insert.Parameters.AddWithValue("@messageId", message.Id);
            insert.Parameters.AddWithValue("@fileName", attachment.FileName ?? string.Empty);
            insert.Parameters.AddWithValue("@contentType", attachment.ContentType ?? "application/octet-stream");
            insert.Parameters.AddWithValue("@size", attachment.Size);
            insert.Parameters.AddWithValue("@localPath", (object?)attachment.LocalPath ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ---------------- FTS 辅助列 ----------------

    private static string NormalizeAddresses(string? json)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var address in JsonHelpers.DeserializeAddresses(json))
        {
            sb.Append(address.Display).Append(' ');
        }
        return FtsTextNormalizer.Normalize(sb.ToString());
    }

    private static string NormalizeFrom(EmailDetail m) =>
        FtsTextNormalizer.Normalize($"{m.FromName} {m.FromEmail}");

    private static string NormalizeTo(EmailDetail m)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in m.To) sb.Append(a.Display).Append(' ');
        foreach (var a in m.Cc) sb.Append(a.Display).Append(' ');
        foreach (var a in m.Bcc) sb.Append(a.Display).Append(' ');
        return FtsTextNormalizer.Normalize(sb.ToString());
    }

    private static string NormalizeBody(EmailDetail m)
    {
        var text = !string.IsNullOrWhiteSpace(m.BodyText) ? m.BodyText : HtmlTextExtractor.ToPlainText(m.BodyHtml);
        return FtsTextNormalizer.Normalize(text);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
