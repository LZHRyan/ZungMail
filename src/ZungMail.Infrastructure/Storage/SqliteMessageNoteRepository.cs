using ZungMail.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>SQLite 邮件备注仓储。</summary>
public sealed class SqliteMessageNoteRepository : IMessageNoteRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMessageNoteRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<string> GetAsync(long emailId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT note FROM message_notes WHERE email_id = @id;";
        command.Parameters.AddWithValue("@id", emailId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value as string ?? string.Empty;
    }

    public async Task SaveAsync(long emailId, string note, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(note))
        {
            command.CommandText = "DELETE FROM message_notes WHERE email_id = @id;";
            command.Parameters.AddWithValue("@id", emailId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return;
        }

        command.CommandText = """
INSERT INTO message_notes(email_id, note, updated_utc) VALUES(@id, @note, @utc)
ON CONFLICT(email_id) DO UPDATE SET note = @note, updated_utc = @utc;
""";
        command.Parameters.AddWithValue("@id", emailId);
        command.Parameters.AddWithValue("@note", note.Trim());
        command.Parameters.AddWithValue("@utc", SqlConvert.ToDb(DateTimeOffset.UtcNow)!);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<long>> GetNotedMessageIdsAsync(
        IReadOnlyList<long> emailIds, CancellationToken ct = default)
    {
        var result = new HashSet<long>();
        if (emailIds.Count == 0) return result;

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var names = new List<string>();
        for (var i = 0; i < emailIds.Count; i++)
        {
            names.Add("@id" + i);
            command.Parameters.AddWithValue("@id" + i, emailIds[i]);
        }

        command.CommandText =
            $"SELECT email_id FROM message_notes WHERE email_id IN ({string.Join(',', names)});";

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(reader.GetInt64(0));
        return result;
    }
}
