using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>待发送队列仓储（FR-08）。</summary>
public sealed class SqliteOutboxRepository : IOutboxRepository
{
    private const string Cols =
        "id, account_id, to_json, cc_json, bcc_json, subject, body_html, is_html, attachments_json, " +
        "scheduled_utc, state, last_error, attempts, created_utc, in_reply_to, \"references\", next_attempt_utc, message_id";

    private readonly SqliteConnectionFactory _factory;

    public SqliteOutboxRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<OutboxItem>> GetDueAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 状态为待发送(0)/失败(3) 且已到点，且不在失败退避等待中，且未用尽自动重试次数。
        command.CommandText = $"""
SELECT {Cols} FROM outbox
WHERE state IN (0, 3)
  AND (scheduled_utc IS NULL OR scheduled_utc <= @now)
  AND (next_attempt_utc IS NULL OR next_attempt_utc <= @now)
  AND attempts < {OutboxItem.MaxAttempts}
ORDER BY created_utc;
""";
        command.Parameters.AddWithValue("@now", SqlConvert.ToDb(nowUtc)!);
        return await ReadListAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Cols} FROM outbox ORDER BY created_utc;";
        return await ReadListAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<OutboxItem?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Cols} FROM outbox WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<long> AddAsync(OutboxItem item, CancellationToken ct = default)
    {
        // 入队即固定 Message-ID：发送、重试与归档复用同一值，避免重复投递无法识别。
        if (string.IsNullOrWhiteSpace(item.MessageId)) item.MessageId = OutboxItem.NewMessageId(null);

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO outbox(account_id, to_json, cc_json, bcc_json, subject, body_html, is_html, attachments_json,
                   scheduled_utc, state, last_error, attempts, created_utc, in_reply_to, "references",
                   next_attempt_utc, message_id)
VALUES(@accountId, @to, @cc, @bcc, @subject, @body, @isHtml, @attachments,
       @scheduled, @state, @error, @attempts, @created, @inReplyTo, @references,
       @next, @messageId);
SELECT last_insert_rowid();
""";
        command.Parameters.AddWithValue("@accountId", item.AccountId);
        command.Parameters.AddWithValue("@to", JsonHelpers.SerializeAddresses(item.To));
        command.Parameters.AddWithValue("@cc", JsonHelpers.SerializeAddresses(item.Cc));
        command.Parameters.AddWithValue("@bcc", JsonHelpers.SerializeAddresses(item.Bcc));
        command.Parameters.AddWithValue("@subject", item.Subject ?? string.Empty);
        command.Parameters.AddWithValue("@body", item.BodyHtml ?? string.Empty);
        command.Parameters.AddWithValue("@isHtml", item.IsHtml ? 1 : 0);
        command.Parameters.AddWithValue("@attachments", JsonHelpers.SerializeStrings(item.AttachmentPaths));
        command.Parameters.AddWithValue("@scheduled", (object?)SqlConvert.ToDb(item.ScheduledUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("@state", (int)item.State);
        command.Parameters.AddWithValue("@error", (object?)item.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("@attempts", item.Attempts);
        command.Parameters.AddWithValue("@created", SqlConvert.ToDb(item.CreatedUtc)!);
        command.Parameters.AddWithValue("@inReplyTo", (object?)item.InReplyTo ?? DBNull.Value);
        command.Parameters.AddWithValue("@references", (object?)item.References ?? DBNull.Value);
        command.Parameters.AddWithValue("@next", (object?)SqlConvert.ToDb(item.NextAttemptUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("@messageId", item.MessageId);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        item.Id = id;
        return id;
    }

    public async Task UpdateStateAsync(long id, OutboxState state, string? error, int attempts, DateTimeOffset? nextAttemptUtc = null, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE outbox SET state = @state, last_error = @error, attempts = @attempts, next_attempt_utc = @next WHERE id = @id;";
        command.Parameters.AddWithValue("@state", (int)state);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@next", (object?)SqlConvert.ToDb(nextAttemptUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM outbox WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<OutboxItem>> ReadListAsync(SqliteCommand command, CancellationToken ct)
    {
        var result = new List<OutboxItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }
        return result;
    }

    private static OutboxItem Map(SqliteDataReader reader) => new()
    {
        Id = SqlConvert.Long(reader, 0),
        AccountId = SqlConvert.Long(reader, 1),
        To = JsonHelpers.DeserializeAddresses(SqlConvert.StrOrNull(reader, 2)),
        Cc = JsonHelpers.DeserializeAddresses(SqlConvert.StrOrNull(reader, 3)),
        Bcc = JsonHelpers.DeserializeAddresses(SqlConvert.StrOrNull(reader, 4)),
        Subject = SqlConvert.Str(reader, 5),
        BodyHtml = SqlConvert.Str(reader, 6),
        IsHtml = SqlConvert.Bool(reader, 7),
        AttachmentPaths = JsonHelpers.DeserializeStrings(SqlConvert.StrOrNull(reader, 8)),
        ScheduledUtc = SqlConvert.ParseDateNullable(reader, 9),
        State = (OutboxState)SqlConvert.Int(reader, 10),
        LastError = SqlConvert.StrOrNull(reader, 11),
        Attempts = SqlConvert.Int(reader, 12),
        CreatedUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 13)),
        InReplyTo = SqlConvert.StrOrNull(reader, 14),
        References = SqlConvert.StrOrNull(reader, 15),
        NextAttemptUtc = SqlConvert.ParseDateNullable(reader, 16),
        MessageId = SqlConvert.Str(reader, 17)
    };
}
