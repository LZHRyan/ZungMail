using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>SQLite 标签仓储。</summary>
public sealed class SqliteMailTagRepository : IMailTagRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMailTagRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<MailTag>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " ORDER BY sort_order, id;";
        var tags = new List<MailTag>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) tags.Add(Map(reader));
        return tags;
    }

    public async Task<IReadOnlyList<MailTag>> GetByAccountAsync(long accountId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE account_id = @account ORDER BY sort_order, id;";
        command.Parameters.AddWithValue("@account", accountId);
        var tags = new List<MailTag>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) tags.Add(Map(reader));
        return tags;
    }

    public async Task<long> UpsertAsync(MailTag tag, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // shortcut_gesture 是旧的组合键列：新模型改用 shortcut_key，旧列保留但不再写入
        command.CommandText = tag.Id == 0
            ? "INSERT INTO mail_tags(name, color, account_id, shortcut_key, sort_order) VALUES(@name, @color, @account, @shortcut, @order); SELECT last_insert_rowid();"
            : "UPDATE mail_tags SET name = @name, color = @color, account_id = @account, shortcut_key = @shortcut, sort_order = @order WHERE id = @id; SELECT @id;";
        if (tag.Id != 0) command.Parameters.AddWithValue("@id", tag.Id);
        command.Parameters.AddWithValue("@name", tag.Name.Trim());
        command.Parameters.AddWithValue("@color", tag.Color);
        command.Parameters.AddWithValue("@account", tag.AccountId);
        command.Parameters.AddWithValue("@shortcut", (object?)tag.ShortcutKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@order", tag.SortOrder);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        tag.Id = id;
        return id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM message_tags WHERE tag_id = @id; DELETE FROM mail_tags WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MailTag>> GetForMessageAsync(long emailId, CancellationToken ct = default)
    {
        var map = await GetForMessagesAsync(new[] { emailId }, ct).ConfigureAwait(false);
        return map.TryGetValue(emailId, out var tags) ? tags : Array.Empty<MailTag>();
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<MailTag>>> GetForMessagesAsync(
        IReadOnlyList<long> emailIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, IReadOnlyList<MailTag>>();
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
SELECT mt.email_id, t.id, t.name, t.color, t.account_id, t.shortcut_key, t.sort_order
FROM message_tags mt JOIN mail_tags t ON t.id = mt.tag_id
WHERE mt.email_id IN ({string.Join(',', names)})
ORDER BY t.sort_order, t.id;
""";

        var buffer = new Dictionary<long, List<MailTag>>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var emailId = reader.GetInt64(0);
            if (!buffer.TryGetValue(emailId, out var list))
            {
                list = new List<MailTag>();
                buffer[emailId] = list;
            }
            list.Add(Map(reader, 1));
        }

        foreach (var (emailId, tags) in buffer) result[emailId] = tags;
        return result;
    }

    public async Task AddToMessageAsync(long emailId, long tagId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO message_tags(email_id, tag_id, created_utc) VALUES(@email, @tag, @utc)
ON CONFLICT(email_id, tag_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("@email", emailId);
        command.Parameters.AddWithValue("@tag", tagId);
        command.Parameters.AddWithValue("@utc", SqlConvert.ToDb(DateTimeOffset.UtcNow)!);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveFromMessageAsync(long emailId, long tagId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM message_tags WHERE email_id = @email AND tag_id = @tag;";
        command.Parameters.AddWithValue("@email", emailId);
        command.Parameters.AddWithValue("@tag", tagId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<long>> GetMessageIdsAsync(long tagId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT email_id FROM message_tags WHERE tag_id = @tag;";
        command.Parameters.AddWithValue("@tag", tagId);
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private const string SelectSql = "SELECT id, name, color, account_id, shortcut_key, sort_order FROM mail_tags";

    private static MailTag Map(SqliteDataReader reader, int offset = 0) => new()
    {
        Id = reader.GetInt64(offset),
        Name = reader.GetString(offset + 1),
        Color = reader.GetString(offset + 2),
        AccountId = reader.IsDBNull(offset + 3) ? 0 : reader.GetInt64(offset + 3),
        ShortcutKey = reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
        SortOrder = reader.GetInt32(offset + 5)
    };
}
