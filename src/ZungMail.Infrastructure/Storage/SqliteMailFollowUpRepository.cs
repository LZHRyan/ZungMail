using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>SQLite「等待回复」跟进仓储。</summary>
public sealed class SqliteMailFollowUpRepository : IMailFollowUpRepository
{
    private const string Columns =
        "id, email_id, thread_id, subject, from_email, sent_utc, recipients, require_all, min_replies, " +
        "include_cc, next_check_utc, state, notified_utc, note, created_utc, updated_utc";

    private readonly SqliteConnectionFactory _factory;

    public SqliteMailFollowUpRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<long> UpsertAsync(MailFollowUp followUp, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        if (followUp.Id == 0)
        {
            command.CommandText = """
INSERT INTO mail_followups(email_id, thread_id, subject, from_email, sent_utc, recipients,
                           require_all, min_replies, include_cc, next_check_utc, state,
                           notified_utc, note, created_utc, updated_utc)
VALUES(@email, @thread, @subject, @from, @sent, @recipients,
       @requireAll, @minReplies, @includeCc, @next, @state,
       @notified, @note, @created, @updated);
SELECT last_insert_rowid();
""";
        }
        else
        {
            command.CommandText = """
UPDATE mail_followups
SET email_id = @email, thread_id = @thread, subject = @subject, from_email = @from,
    sent_utc = @sent, recipients = @recipients, require_all = @requireAll,
    min_replies = @minReplies, include_cc = @includeCc, next_check_utc = @next,
    state = @state, notified_utc = @notified, note = @note, updated_utc = @updated
WHERE id = @id;
SELECT @id;
""";
            command.Parameters.AddWithValue("@id", followUp.Id);
        }

        var now = SqlConvert.ToDb(DateTimeOffset.UtcNow)!;
        command.Parameters.AddWithValue("@email", followUp.EmailId);
        command.Parameters.AddWithValue("@thread", followUp.ThreadId ?? string.Empty);
        command.Parameters.AddWithValue("@subject", followUp.Subject ?? string.Empty);
        command.Parameters.AddWithValue("@from", followUp.FromEmail ?? string.Empty);
        command.Parameters.AddWithValue("@sent", SqlConvert.ToDb(followUp.SentUtc)!);
        command.Parameters.AddWithValue("@recipients", JsonHelpers.SerializeStrings(followUp.Recipients));
        command.Parameters.AddWithValue("@requireAll", followUp.RequireAll ? 1 : 0);
        command.Parameters.AddWithValue("@minReplies", Math.Max(1, followUp.MinReplies));
        command.Parameters.AddWithValue("@includeCc", followUp.IncludeCc ? 1 : 0);
        command.Parameters.AddWithValue("@next", SqlConvert.ToDb(followUp.NextCheckUtc)!);
        command.Parameters.AddWithValue("@state", (int)followUp.State);
        command.Parameters.AddWithValue("@notified", (object?)SqlConvert.ToDb(followUp.NotifiedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("@note", followUp.Note ?? string.Empty);
        command.Parameters.AddWithValue(
            "@created",
            SqlConvert.ToDb(followUp.CreatedUtc == default ? DateTimeOffset.UtcNow : followUp.CreatedUtc)!);
        command.Parameters.AddWithValue("@updated", now);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        followUp.Id = id;
        return id;
    }

    public async Task<MailFollowUp?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM mail_followups WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<MailFollowUp>> GetAllAsync(
        FollowUpState? state = null,
        int take = 100,
        CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM mail_followups" +
                              (state.HasValue ? " WHERE state = @state" : "") +
                              " ORDER BY created_utc DESC, id DESC LIMIT @take;";
        if (state.HasValue) command.Parameters.AddWithValue("@state", (int)state.Value);
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 500));

        var items = new List<MailFollowUp>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(Map(reader));
        return items;
    }

    public async Task<IReadOnlyList<MailFollowUp>> GetDueAsync(
        DateTimeOffset now,
        int take = 20,
        CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT {Columns} FROM mail_followups
WHERE state = 0 AND notified_utc IS NULL AND next_check_utc <= @now
ORDER BY next_check_utc
LIMIT @take;
""";
        command.Parameters.AddWithValue("@now", SqlConvert.ToDb(now)!);
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 200));

        var items = new List<MailFollowUp>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(Map(reader));
        return items;
    }

    public async Task<IReadOnlyList<FollowUpReply>> GetThreadMessagesAfterAsync(
        string threadId,
        long excludeEmailId,
        DateTimeOffset afterUtc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return Array.Empty<FollowUpReply>();

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, COALESCE(from_email, ''), COALESCE(from_name, ''), COALESCE(subject, ''), date_utc
FROM messages
WHERE thread_id = @thread
  AND date_utc > @after
  AND id <> @exclude
  AND (flags & 32) = 0
ORDER BY date_utc;
""";
        command.Parameters.AddWithValue("@thread", threadId);
        command.Parameters.AddWithValue("@after", SqlConvert.ToDb(afterUtc)!);
        command.Parameters.AddWithValue("@exclude", excludeEmailId);

        var items = new List<FollowUpReply>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new FollowUpReply(
                reader.GetInt64(0),
                SqlConvert.Str(reader, 1),
                SqlConvert.Str(reader, 2),
                SqlConvert.Str(reader, 3),
                SqlConvert.ParseDate(SqlConvert.Str(reader, 4))));
        }
        return items;
    }

    public async Task SetStateAsync(long id, FollowUpState state, DateTimeOffset utc, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mail_followups SET state = @state, updated_utc = @utc WHERE id = @id;";
        command.Parameters.AddWithValue("@state", (int)state);
        command.Parameters.AddWithValue("@utc", SqlConvert.ToDb(utc)!);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RescheduleAsync(long id, DateTimeOffset nextCheckUtc, DateTimeOffset utc, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE mail_followups
SET next_check_utc = @next, notified_utc = NULL, updated_utc = @utc
WHERE id = @id;
""";
        command.Parameters.AddWithValue("@next", SqlConvert.ToDb(nextCheckUtc)!);
        command.Parameters.AddWithValue("@utc", SqlConvert.ToDb(utc)!);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkNotifiedAsync(long id, DateTimeOffset utc, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mail_followups SET notified_utc = @utc, updated_utc = @utc WHERE id = @id;";
        command.Parameters.AddWithValue("@utc", SqlConvert.ToDb(utc)!);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mail_followups WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountActiveAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_followups WHERE state = 0;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    private static MailFollowUp Map(SqliteDataReader reader) => new()
    {
        Id = SqlConvert.Long(reader, 0),
        EmailId = SqlConvert.Long(reader, 1),
        ThreadId = SqlConvert.Str(reader, 2),
        Subject = SqlConvert.Str(reader, 3),
        FromEmail = SqlConvert.Str(reader, 4),
        SentUtc = SqlConvert.ParseDate(SqlConvert.Str(reader, 5)),
        Recipients = JsonHelpers.DeserializeStrings(SqlConvert.StrOrNull(reader, 6)),
        RequireAll = SqlConvert.Bool(reader, 7),
        MinReplies = SqlConvert.Int(reader, 8),
        IncludeCc = SqlConvert.Bool(reader, 9),
        NextCheckUtc = SqlConvert.ParseDate(SqlConvert.Str(reader, 10)),
        State = (FollowUpState)SqlConvert.Int(reader, 11),
        NotifiedUtc = SqlConvert.ParseDateNullable(reader, 12),
        Note = SqlConvert.Str(reader, 13),
        CreatedUtc = SqlConvert.ParseDate(SqlConvert.Str(reader, 14)),
        UpdatedUtc = SqlConvert.ParseDate(SqlConvert.Str(reader, 15))
    };
}
