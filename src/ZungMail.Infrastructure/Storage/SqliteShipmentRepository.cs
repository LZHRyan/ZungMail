using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// SQLite 一票货（案件）与时效节点仓储。
/// <para>
/// 归并策略：一封邮件抽出多个单号时，先把每个单号已经归属的案件都找出来；
/// 只要命中任意一个就并入「最早创建」的那个案件（其余案件与之合并），
/// 否则以优先级最高的单号（Job &gt; SO &gt; B/L &gt; AWB &gt; 箱号 &gt; 封号）新建案件。
/// 这样同一票货先收到提单草稿、后收到 SO 时不会裂成两个案件。
/// </para>
/// </summary>
public sealed class SqliteShipmentRepository : IShipmentRepository
{
    /// <summary>可作为案件别名的标识类型；港口 / 发票 / PO 过于泛化，不参与归并。</summary>
    private static readonly FreightReferenceKind[] AliasKinds =
    {
        FreightReferenceKind.Job,
        FreightReferenceKind.Booking,
        FreightReferenceKind.BillOfLading,
        FreightReferenceKind.AirWaybill,
        FreightReferenceKind.Container,
        FreightReferenceKind.Seal
    };

    private const string DeadlineColumns = """
SELECT d.email_id,
       COALESCE((SELECT cm.case_id FROM case_messages cm
                 WHERE cm.email_id = d.email_id ORDER BY cm.case_id LIMIT 1), 0) AS case_id,
       COALESCE((SELECT c.primary_value FROM case_messages cm
                 JOIN shipment_cases c ON c.id = cm.case_id
                 WHERE cm.email_id = d.email_id ORDER BY cm.case_id LIMIT 1), '') AS case_ref,
       d.kind, d.due_utc, d.raw_text,
       COALESCE(m.subject, ''), COALESCE(m.from_name, ''), COALESCE(m.from_email, ''), m.date_utc
FROM message_deadlines d
JOIN messages m ON m.id = d.email_id
""";

    private const string CaseColumns = """
SELECT c.id, c.primary_kind, c.primary_value, c.status,
       (SELECT COUNT(*) FROM case_messages cm WHERE cm.case_id = c.id) AS message_count,
       c.last_activity_utc,
       (SELECT MIN(d.due_utc) FROM message_deadlines d
          JOIN case_messages cm2 ON cm2.email_id = d.email_id
        WHERE cm2.case_id = c.id AND d.due_utc >= @now) AS next_due,
       c.stage, c.exception
FROM shipment_cases c
WHERE EXISTS (SELECT 1 FROM case_messages cm3 WHERE cm3.case_id = c.id)
""";

    private readonly SqliteConnectionFactory _factory;

    public SqliteShipmentRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task ApplyAsync(IReadOnlyList<ShipmentIndexEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = SqlConvert.ToDb(DateTimeOffset.UtcNow)!;

        foreach (var entry in entries)
        {
            await ReplaceDeadlinesAsync(connection, tx, entry, now, ct).ConfigureAwait(false);
            await RelinkMessageAsync(connection, tx, entry, now, ct).ConfigureAwait(false);
        }

        await CleanupAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeadlineBoardItem>> GetUpcomingDeadlinesAsync(
        int take = 8,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = DeadlineColumns + """

WHERE d.due_utc >= @from
  AND (m.flags & 32) = 0
  AND NOT EXISTS (
      SELECT 1 FROM case_messages cx
      JOIN shipment_cases cx2 ON cx2.id = cx.case_id
      WHERE cx.email_id = d.email_id AND cx2.status = 1)
ORDER BY d.due_utc
LIMIT @take;
""";
        command.Parameters.AddWithValue("@from", SqlConvert.ToDb(fromUtc ?? DateTimeOffset.UtcNow.AddHours(-24))!);
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 100));

        var items = new List<DeadlineBoardItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(ReadDeadline(reader));
        return items;
    }

    public async Task<IReadOnlyList<DeadlineBoardItem>> GetDeadlinesForMessageAsync(
        long emailId,
        CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = DeadlineColumns + """

WHERE d.email_id = @email
ORDER BY d.due_utc;
""";
        command.Parameters.AddWithValue("@email", emailId);

        var items = new List<DeadlineBoardItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(ReadDeadline(reader));
        return items;
    }

    public async Task<IReadOnlyList<ShipmentCaseSummary>> GetRecentCasesAsync(
        int take = 20,
        CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = CaseColumns + """

ORDER BY c.last_activity_utc DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("@now", SqlConvert.ToDb(DateTimeOffset.UtcNow)!);
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 200));

        var items = new List<ShipmentCaseSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(ReadCase(reader));
        return items;
    }

    public async Task<ShipmentCaseDetail?> GetCaseAsync(long caseId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        ShipmentCaseSummary? summary;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = CaseColumns + " AND c.id = @id;";
            command.Parameters.AddWithValue("@now", SqlConvert.ToDb(DateTimeOffset.UtcNow)!);
            command.Parameters.AddWithValue("@id", caseId);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            summary = ReadCase(reader);
        }

        if (summary is null) return null;

        var messages = new List<CaseMessageRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT m.id, COALESCE(m.subject, ''), COALESCE(m.from_name, ''), COALESCE(m.from_email, ''),
       m.date_utc, m.has_attachment, m.flags, m.mail_type
FROM case_messages cm
JOIN messages m ON m.id = cm.email_id
WHERE cm.case_id = @id
ORDER BY m.date_utc, m.id;
""";
            command.Parameters.AddWithValue("@id", caseId);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                messages.Add(new CaseMessageRow(
                    reader.GetInt64(0),
                    SqlConvert.Str(reader, 1),
                    SqlConvert.Str(reader, 2),
                    SqlConvert.Str(reader, 3),
                    SqlConvert.ParseDate(SqlConvert.Str(reader, 4)),
                    SqlConvert.Bool(reader, 5),
                    (SqlConvert.Long(reader, 6) & (long)MessageFlags.Seen) == 0,
                    (MailType)SqlConvert.Int(reader, 7)));
            }
        }

        var deadlines = new List<DeadlineBoardItem>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = DeadlineColumns + """

WHERE d.email_id IN (SELECT email_id FROM case_messages WHERE case_id = @id)
ORDER BY d.due_utc;
""";
            command.Parameters.AddWithValue("@id", caseId);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) deadlines.Add(ReadDeadline(reader));
        }

        // 同一票货里多封邮件会重复提到同一个节点（SO 与补料邮件都写 SI CUT-OFF），按「类型 + 时间」去重
        deadlines = deadlines
            .GroupBy(d => (d.Kind, d.DueUtc))
            .Select(g => g.First())
            .OrderBy(d => d.DueUtc)
            .ThenBy(d => d.Kind)
            .ToList();

        var attachments = new List<CaseAttachmentRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT a.message_id, COALESCE(a.file_name, ''), a.local_path, a.size, COALESCE(m.subject, '')
FROM attachments a
JOIN case_messages cm ON cm.email_id = a.message_id
JOIN messages m ON m.id = a.message_id
WHERE cm.case_id = @id AND a.is_inline = 0
ORDER BY m.date_utc, a.id;
""";
            command.Parameters.AddWithValue("@id", caseId);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                attachments.Add(new CaseAttachmentRow(
                    reader.GetInt64(0),
                    SqlConvert.Str(reader, 1),
                    SqlConvert.StrOrNull(reader, 2),
                    SqlConvert.Long(reader, 3),
                    SqlConvert.Str(reader, 4)));
            }
        }

        return new ShipmentCaseDetail(summary, messages, deadlines, attachments);
    }

    public async Task<long> GetCaseIdForMessageAsync(long emailId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT case_id FROM case_messages WHERE email_id = @email ORDER BY case_id LIMIT 1;";
        command.Parameters.AddWithValue("@email", emailId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0L : Convert.ToInt64(value);
    }

    public async Task SetCaseStatusAsync(long caseId, ShipmentCaseStatus status, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE shipment_cases SET status = @status WHERE id = @id;";
        command.Parameters.AddWithValue("@status", (int)status);
        command.Parameters.AddWithValue("@id", caseId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ShipmentCaseExport?> GetCaseExportAsync(long caseId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        string primaryRef;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT primary_value FROM shipment_cases WHERE id = @id;";
            command.Parameters.AddWithValue("@id", caseId);
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value is null or DBNull) return null;
            primaryRef = Convert.ToString(value) ?? "";
        }

        var attachmentsByMessage = new Dictionary<long, List<CaseExportAttachment>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT a.message_id, COALESCE(a.file_name, ''), a.size, a.local_path
FROM attachments a
JOIN case_messages cm ON cm.email_id = a.message_id
WHERE cm.case_id = @id AND a.is_inline = 0
ORDER BY a.message_id, a.id;
""";
            command.Parameters.AddWithValue("@id", caseId);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var emailId = reader.GetInt64(0);
                if (!attachmentsByMessage.TryGetValue(emailId, out var list))
                {
                    list = new List<CaseExportAttachment>();
                    attachmentsByMessage[emailId] = list;
                }
                list.Add(new CaseExportAttachment(
                    SqlConvert.Str(reader, 1),
                    SqlConvert.Long(reader, 2),
                    SqlConvert.StrOrNull(reader, 3)));
            }
        }

        var messages = new List<CaseExportMessage>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT m.id, COALESCE(m.subject, ''), COALESCE(m.from_name, ''), COALESCE(m.from_email, ''),
       m.to_json, m.cc_json, m.date_utc, m.eml_path, m.body_html, m.body_text
FROM case_messages cm
JOIN messages m ON m.id = cm.email_id
WHERE cm.case_id = @id
ORDER BY m.date_utc, m.id;
""";
            command.Parameters.AddWithValue("@id", caseId);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var emailId = reader.GetInt64(0);
                var fromName = SqlConvert.Str(reader, 2);
                var fromEmail = SqlConvert.Str(reader, 3);
                messages.Add(new CaseExportMessage(
                    emailId,
                    SqlConvert.Str(reader, 1),
                    string.IsNullOrWhiteSpace(fromName) ? fromEmail : fromName,
                    JoinAddresses(SqlConvert.StrOrNull(reader, 4)),
                    JoinAddresses(SqlConvert.StrOrNull(reader, 5)),
                    SqlConvert.ParseDate(SqlConvert.Str(reader, 6)),
                    SqlConvert.StrOrNull(reader, 7),
                    SqlConvert.StrOrNull(reader, 8),
                    SqlConvert.StrOrNull(reader, 9),
                    attachmentsByMessage.TryGetValue(emailId, out var list)
                        ? list
                        : Array.Empty<CaseExportAttachment>()));
            }
        }

        return new ShipmentCaseExport(caseId, primaryRef, messages);
    }

    private static string JoinAddresses(string? json)
        => string.Join("; ", JsonHelpers.DeserializeAddresses(json).Select(a => a.Display));

    private static DeadlineBoardItem ReadDeadline(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            SqlConvert.Str(reader, 2),
            (FreightDeadlineKind)SqlConvert.Int(reader, 3),
            SqlConvert.ParseDate(SqlConvert.Str(reader, 4)),
            SqlConvert.Str(reader, 5),
            SqlConvert.Str(reader, 6),
            string.IsNullOrWhiteSpace(SqlConvert.Str(reader, 7))
                ? SqlConvert.Str(reader, 8)
                : SqlConvert.Str(reader, 7),
            SqlConvert.ParseDate(SqlConvert.Str(reader, 9)));

    private static ShipmentCaseSummary ReadCase(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            (FreightReferenceKind)SqlConvert.Int(reader, 1),
            SqlConvert.Str(reader, 2),
            (ShipmentCaseStatus)SqlConvert.Int(reader, 3),
            SqlConvert.Int(reader, 4),
            SqlConvert.ParseDate(SqlConvert.Str(reader, 5)),
            SqlConvert.ParseDateNullable(reader, 6),
            (CaseStage)SqlConvert.Int(reader, 7),
            SqlConvert.Bool(reader, 8));

    // ---------------- 写入 ----------------

    private static async Task ReplaceDeadlinesAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ShipmentIndexEntry entry,
        string now,
        CancellationToken ct)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM message_deadlines WHERE email_id = @email;";
            delete.Parameters.AddWithValue("@email", entry.EmailId);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var deadline in entry.Deadlines)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
INSERT OR IGNORE INTO message_deadlines(email_id, kind, due_utc, raw_text, created_utc)
VALUES(@email, @kind, @due, @raw, @utc);
""";
            insert.Parameters.AddWithValue("@email", entry.EmailId);
            insert.Parameters.AddWithValue("@kind", (int)deadline.Kind);
            insert.Parameters.AddWithValue("@due", SqlConvert.ToDb(deadline.DueUtc)!);
            insert.Parameters.AddWithValue("@raw", deadline.RawText);
            insert.Parameters.AddWithValue("@utc", now);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task RelinkMessageAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ShipmentIndexEntry entry,
        string now,
        CancellationToken ct)
    {
        await using (var unlink = connection.CreateCommand())
        {
            unlink.Transaction = tx;
            unlink.CommandText = "DELETE FROM case_messages WHERE email_id = @email;";
            unlink.Parameters.AddWithValue("@email", entry.EmailId);
            await unlink.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var aliases = entry.References
            .Where(r => AliasKinds.Contains(r.Kind) && r.Normalized.Length >= 4)
            .GroupBy(r => (r.Kind, r.Normalized))
            .Select(g => g.First())
            .ToList();
        if (aliases.Count == 0) return;

        var candidateIds = new SortedSet<long>();
        foreach (var alias in aliases)
        {
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = tx;
            lookup.CommandText = "SELECT case_id FROM case_refs WHERE kind = @kind AND normalized = @normalized LIMIT 1;";
            lookup.Parameters.AddWithValue("@kind", (int)alias.Kind);
            lookup.Parameters.AddWithValue("@normalized", alias.Normalized);
            var value = await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value is not null and not DBNull) candidateIds.Add(Convert.ToInt64(value));
        }

        long caseId;
        if (candidateIds.Count == 0)
        {
            caseId = await CreateCaseAsync(connection, tx, aliases, entry.MessageDate, now, ct).ConfigureAwait(false);
        }
        else
        {
            caseId = candidateIds.Min;
            foreach (var duplicate in candidateIds.Where(id => id != caseId))
            {
                await MergeCaseAsync(connection, tx, duplicate, caseId, ct).ConfigureAwait(false);
            }
        }

        foreach (var alias in aliases)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
INSERT OR IGNORE INTO case_refs(case_id, kind, normalized, value)
VALUES(@case, @kind, @normalized, @value);
""";
            insert.Parameters.AddWithValue("@case", caseId);
            insert.Parameters.AddWithValue("@kind", (int)alias.Kind);
            insert.Parameters.AddWithValue("@normalized", alias.Normalized);
            insert.Parameters.AddWithValue("@value", alias.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var link = connection.CreateCommand())
        {
            link.Transaction = tx;
            link.CommandText = """
INSERT OR IGNORE INTO case_messages(case_id, email_id, linked_utc)
VALUES(@case, @email, @utc);
""";
            link.Parameters.AddWithValue("@case", caseId);
            link.Parameters.AddWithValue("@email", entry.EmailId);
            link.Parameters.AddWithValue("@utc", now);
            await link.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = tx;
            touch.CommandText = """
UPDATE shipment_cases
SET last_activity_utc = CASE WHEN last_activity_utc < @date THEN @date ELSE last_activity_utc END
WHERE id = @id;
""";
            touch.Parameters.AddWithValue("@date", SqlConvert.ToDb(entry.MessageDate)!);
            touch.Parameters.AddWithValue("@id", caseId);
            await touch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 行业类型推进案件阶段（只前进），异常类型只打标记不改阶段
        var stage = CaseStages.FromMailType(entry.MailType);
        var isException = MailTypes.IsException(entry.MailType);
        if (stage != CaseStage.New || isException)
        {
            await using var progress = connection.CreateCommand();
            progress.Transaction = tx;
            progress.CommandText = """
UPDATE shipment_cases
SET stage = MAX(stage, @stage), exception = MAX(exception, @exception)
WHERE id = @id;
""";
            progress.Parameters.AddWithValue("@stage", (int)stage);
            progress.Parameters.AddWithValue("@exception", isException ? 1 : 0);
            progress.Parameters.AddWithValue("@id", caseId);
            await progress.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await UpgradePrimaryAsync(connection, tx, caseId, aliases, ct).ConfigureAwait(false);
    }

    private static async Task<long> CreateCaseAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        IReadOnlyList<FreightReference> aliases,
        DateTimeOffset messageDate,
        string now,
        CancellationToken ct)
    {
        var primary = aliases.OrderBy(r => RankOf(r.Kind)).ThenBy(r => r.Normalized, StringComparer.Ordinal).First();

        // 唯一索引 (primary_kind, primary_ref) 兜底：历史数据可能已经建过同名案件
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT id FROM shipment_cases WHERE primary_kind = @kind AND primary_ref = @ref LIMIT 1;";
            existing.Parameters.AddWithValue("@kind", (int)primary.Kind);
            existing.Parameters.AddWithValue("@ref", primary.Normalized);
            var value = await existing.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value is not null and not DBNull) return Convert.ToInt64(value);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
INSERT INTO shipment_cases(primary_kind, primary_ref, primary_value, status, created_utc, last_activity_utc)
VALUES(@kind, @ref, @value, 0, @utc, @date);
""";
        insert.Parameters.AddWithValue("@kind", (int)primary.Kind);
        insert.Parameters.AddWithValue("@ref", primary.Normalized);
        insert.Parameters.AddWithValue("@value", primary.Value);
        insert.Parameters.AddWithValue("@utc", now);
        insert.Parameters.AddWithValue("@date", SqlConvert.ToDb(messageDate)!);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var id = connection.CreateCommand();
        id.Transaction = tx;
        id.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(await id.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    private static async Task MergeCaseAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        long duplicate,
        long keep,
        CancellationToken ct)
    {
        if (duplicate == keep) return;

        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = tx;
            touch.CommandText = """
UPDATE shipment_cases
SET last_activity_utc = MAX(last_activity_utc, (SELECT last_activity_utc FROM shipment_cases WHERE id = @duplicate))
WHERE id = @keep;
""";
            touch.Parameters.AddWithValue("@duplicate", duplicate);
            touch.Parameters.AddWithValue("@keep", keep);
            await touch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var moveRefs = connection.CreateCommand())
        {
            moveRefs.Transaction = tx;
            moveRefs.CommandText = "UPDATE OR IGNORE case_refs SET case_id = @keep WHERE case_id = @duplicate;";
            moveRefs.Parameters.AddWithValue("@keep", keep);
            moveRefs.Parameters.AddWithValue("@duplicate", duplicate);
            await moveRefs.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var dropRefs = connection.CreateCommand())
        {
            dropRefs.Transaction = tx;
            dropRefs.CommandText = "DELETE FROM case_refs WHERE case_id = @duplicate;";
            dropRefs.Parameters.AddWithValue("@duplicate", duplicate);
            await dropRefs.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var moveMessages = connection.CreateCommand())
        {
            moveMessages.Transaction = tx;
            moveMessages.CommandText = "UPDATE OR IGNORE case_messages SET case_id = @keep WHERE case_id = @duplicate;";
            moveMessages.Parameters.AddWithValue("@keep", keep);
            moveMessages.Parameters.AddWithValue("@duplicate", duplicate);
            await moveMessages.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var dropMessages = connection.CreateCommand())
        {
            dropMessages.Transaction = tx;
            dropMessages.CommandText = "DELETE FROM case_messages WHERE case_id = @duplicate;";
            dropMessages.Parameters.AddWithValue("@duplicate", duplicate);
            await dropMessages.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var dropCase = connection.CreateCommand();
        dropCase.Transaction = tx;
        dropCase.CommandText = "DELETE FROM shipment_cases WHERE id = @duplicate;";
        dropCase.Parameters.AddWithValue("@duplicate", duplicate);
        await dropCase.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>后到的邮件若带来更高优先级的单号（先有提单后收到 SO），把案件主键升级。</summary>
    private static async Task UpgradePrimaryAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        long caseId,
        IReadOnlyList<FreightReference> aliases,
        CancellationToken ct)
    {
        var best = aliases.OrderBy(r => RankOf(r.Kind)).ThenBy(r => r.Normalized, StringComparer.Ordinal).First();

        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
UPDATE OR IGNORE shipment_cases
SET primary_kind = @kind, primary_ref = @ref, primary_value = @value
WHERE id = @id AND @rank < (CASE primary_kind
    WHEN 0 THEN 0 WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 3 WHEN 4 THEN 4 ELSE 5 END);
""";
        update.Parameters.AddWithValue("@kind", (int)best.Kind);
        update.Parameters.AddWithValue("@ref", best.Normalized);
        update.Parameters.AddWithValue("@value", best.Value);
        update.Parameters.AddWithValue("@rank", RankOf(best.Kind));
        update.Parameters.AddWithValue("@id", caseId);
        await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task CleanupAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await using (var cases = connection.CreateCommand())
        {
            cases.Transaction = tx;
            cases.CommandText = """
DELETE FROM shipment_cases
WHERE id NOT IN (SELECT DISTINCT case_id FROM case_messages);
""";
            await cases.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var refs = connection.CreateCommand();
        refs.Transaction = tx;
        refs.CommandText = "DELETE FROM case_refs WHERE case_id NOT IN (SELECT id FROM shipment_cases);";
        await refs.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>案件主键优先级：内部 Job 号最稳定，其次是 SO，再次是提单 / 空运单 / 箱号 / 封号。</summary>
    private static int RankOf(FreightReferenceKind kind) => kind switch
    {
        FreightReferenceKind.Job => 0,
        FreightReferenceKind.Booking => 1,
        FreightReferenceKind.BillOfLading => 2,
        FreightReferenceKind.AirWaybill => 3,
        FreightReferenceKind.Container => 4,
        _ => 5
    };
}
