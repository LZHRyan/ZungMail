using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>SQLite 货代业务标识仓储。</summary>
public sealed class SqliteMailReferenceRepository : IMailReferenceRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMailReferenceRepository(SqliteConnectionFactory factory) => _factory = factory;

    public Task ReplaceForMessageAsync(
        long emailId,
        IReadOnlyList<FreightReference> references,
        bool bodyIndexed,
        CancellationToken ct = default)
        => ReplaceRangeAsync(new[] { new ReferenceIndexEntry(emailId, bodyIndexed, references) }, ct);

    public async Task ReplaceRangeAsync(IReadOnlyList<ReferenceIndexEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = SqlConvert.ToDb(DateTimeOffset.UtcNow)!;

        foreach (var entry in entries)
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM message_refs WHERE email_id = @email;";
                delete.Parameters.AddWithValue("@email", entry.EmailId);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var seen = new HashSet<(int Kind, string Normalized)>();
            foreach (var reference in entry.References)
            {
                if (reference.Normalized.Length < 2) continue;
                if (!seen.Add(((int)reference.Kind, reference.Normalized))) continue;

                await using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
INSERT OR IGNORE INTO message_refs(email_id, kind, value, normalized, source, created_utc)
VALUES(@email, @kind, @value, @normalized, @source, @utc);
""";
                insert.Parameters.AddWithValue("@email", entry.EmailId);
                insert.Parameters.AddWithValue("@kind", (int)reference.Kind);
                insert.Parameters.AddWithValue("@value", reference.Value);
                insert.Parameters.AddWithValue("@normalized", reference.Normalized);
                insert.Parameters.AddWithValue("@source", (int)reference.Source);
                insert.Parameters.AddWithValue("@utc", now);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var scan = connection.CreateCommand();
            scan.Transaction = tx;
            scan.CommandText = """
INSERT INTO message_ref_scan(email_id, body_indexed, scanned_utc)
VALUES(@email, @body, @utc)
ON CONFLICT(email_id) DO UPDATE SET
    body_indexed = MAX(message_ref_scan.body_indexed, excluded.body_indexed),
    scanned_utc  = excluded.scanned_utc;
""";
            scan.Parameters.AddWithValue("@email", entry.EmailId);
            scan.Parameters.AddWithValue("@body", entry.BodyIndexed ? 1 : 0);
            scan.Parameters.AddWithValue("@utc", now);
            await scan.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // 行业类型与单号同一批写入：重扫时类型也会被重算（Unknown 表示这张表还没有识别结果）
            await using var type = connection.CreateCommand();
            type.Transaction = tx;
            type.CommandText = "UPDATE messages SET mail_type = @type, mail_type_score = @score, mail_type_basis = @basis WHERE id = @email;";
            type.Parameters.AddWithValue("@type", (int)entry.MailType);
            type.Parameters.AddWithValue("@score", entry.MailTypeScore);
            type.Parameters.AddWithValue("@basis", entry.MailTypeBasis ?? "");
            type.Parameters.AddWithValue("@email", entry.EmailId);
            await type.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> ApplyThreadInheritanceAsync(int maxUpdates = 500, CancellationToken ct = default)
    {
        if (maxUpdates <= 0) return 0;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

        // 已确定类型的邮件按会话聚合：先用纯函数算出每个会话的赢家（异常类优先，其次多数）
        var counts = new Dictionary<string, Dictionary<MailType, int>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT thread_id, mail_type, COUNT(*)
FROM messages
WHERE mail_type <> 0 AND thread_id IS NOT NULL AND thread_id <> ''
GROUP BY thread_id, mail_type;
""";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var thread = reader.GetString(0);
                var type = (MailType)reader.GetInt32(1);
                var count = reader.GetInt32(2);

                if (!counts.TryGetValue(thread, out var perType))
                {
                    counts[thread] = perType = new Dictionary<MailType, int>();
                }

                perType[type] = perType.TryGetValue(type, out var current) ? current + count : count;
            }
        }

        var winners = new Dictionary<string, MailType>(StringComparer.Ordinal);
        foreach (var (thread, perType) in counts)
        {
            var winner = MailTypeThreadInheritance.PickWinner(perType);
            if (winner != MailType.Unknown) winners[thread] = winner;
        }

        if (winners.Count == 0) return 0;

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
UPDATE messages
SET mail_type = @type, mail_type_score = 1, mail_type_basis = 'thread:inherit'
WHERE thread_id = @thread AND mail_type = 0;
""";
        var typeParameter = update.Parameters.Add("@type", SqliteType.Integer);
        var threadParameter = update.Parameters.Add("@thread", SqliteType.Text);

        var updated = 0;
        foreach (var (thread, winner) in winners)
        {
            typeParameter.Value = (int)winner;
            threadParameter.Value = thread;
            updated += await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (updated >= maxUpdates) break;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<IReadOnlyList<FreightReference>> GetForMessageAsync(long emailId, CancellationToken ct = default)
    {
        var map = await GetForMessagesAsync(new[] { emailId }, ct).ConfigureAwait(false);
        return map.TryGetValue(emailId, out var refs) ? refs : Array.Empty<FreightReference>();
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<FreightReference>>> GetForMessagesAsync(
        IReadOnlyList<long> emailIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, IReadOnlyList<FreightReference>>();
        if (emailIds.Count == 0) return result;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var names = new List<string>();
        for (var i = 0; i < emailIds.Count; i++)
        {
            names.Add("@id" + i);
            command.Parameters.AddWithValue("@id" + i, emailIds[i]);
        }

        command.CommandText = $"""
SELECT email_id, kind, value, normalized, source
FROM message_refs
WHERE email_id IN ({string.Join(',', names)})
ORDER BY kind, value;
""";

        var buffer = new Dictionary<long, List<FreightReference>>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var emailId = reader.GetInt64(0);
            if (!buffer.TryGetValue(emailId, out var list))
            {
                list = new List<FreightReference>();
                buffer[emailId] = list;
            }

            list.Add(new FreightReference
            {
                Kind = (FreightReferenceKind)SqlConvert.Int(reader, 1),
                Value = SqlConvert.Str(reader, 2),
                Normalized = SqlConvert.Str(reader, 3),
                Source = (ReferenceSource)SqlConvert.Int(reader, 4)
            });
        }

        foreach (var (emailId, list) in buffer)
        {
            result[emailId] = list.OrderBy(r => r.Priority).ThenBy(r => r.Value, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return result;
    }

    public async Task<IReadOnlyList<FreightReferenceSummary>> GetTopAsync(
        long? accountId = null,
        int take = 12,
        CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var accountFilter = accountId.HasValue ? "AND m.account_id = @account" : string.Empty;
        if (accountId.HasValue) command.Parameters.AddWithValue("@account", accountId.Value);
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 100));

        command.CommandText = $"""
SELECT r.kind, r.normalized, MAX(r.value) AS value,
       COUNT(DISTINCT r.email_id) AS message_count, MAX(m.date_utc) AS last_utc
FROM message_refs r
JOIN messages m ON m.id = r.email_id
JOIN folders f ON f.id = m.folder_id
WHERE (m.flags & 32) = 0 AND f.kind <> 4 {accountFilter}
GROUP BY r.kind, r.normalized
ORDER BY
    CASE r.kind WHEN 0 THEN 0 WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 3
                WHEN 9 THEN 4 WHEN 7 THEN 5 WHEN 8 THEN 6 ELSE 7 END,
    last_utc DESC,
    message_count DESC
LIMIT @take;
""";

        var items = new List<FreightReferenceSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new FreightReferenceSummary(
                (FreightReferenceKind)SqlConvert.Int(reader, 0),
                SqlConvert.Str(reader, 2),
                SqlConvert.Str(reader, 1),
                SqlConvert.Int(reader, 3),
                SqlConvert.ParseDate(SqlConvert.Str(reader, 4))));
        }
        return items;
    }

    public async Task<IReadOnlyList<PendingReferenceScan>> GetPendingScansAsync(int take = 200, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT m.id, COALESCE(m.subject, ''), COALESCE(m.preview, ''), COALESCE(m.body_text, ''),
       CASE WHEN m.body_state = 1 THEN 1 ELSE 0 END,
       m.date_utc
FROM messages m
LEFT JOIN message_ref_scan s ON s.email_id = m.id
WHERE (m.flags & 32) = 0
  AND (s.email_id IS NULL OR (s.body_indexed = 0 AND m.body_state = 1))
ORDER BY m.date_utc DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 2000));

        var items = new List<PendingReferenceScan>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new PendingReferenceScan(
                reader.GetInt64(0),
                SqlConvert.Str(reader, 1),
                SqlConvert.Str(reader, 2),
                SqlConvert.Str(reader, 3),
                SqlConvert.Int(reader, 4) != 0,
                SqlConvert.ParseDate(SqlConvert.Str(reader, 5))));
        }
        return items;
    }

    public async Task<int> CountPendingScansAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM messages m
LEFT JOIN message_ref_scan s ON s.email_id = m.id
WHERE (m.flags & 32) = 0
  AND (s.email_id IS NULL OR (s.body_indexed = 0 AND m.body_state = 1));
""";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    public async Task InvalidateAsync(long emailId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO message_ref_scan(email_id, body_indexed, scanned_utc)
VALUES(@email, 0, @utc)
ON CONFLICT(email_id) DO UPDATE SET body_indexed = 0, scanned_utc = excluded.scanned_utc;
""";
        command.Parameters.AddWithValue("@email", emailId);
        command.Parameters.AddWithValue("@utc", SqlConvert.ToDb(DateTimeOffset.UtcNow)!);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearScanProgressAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM message_ref_scan;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
