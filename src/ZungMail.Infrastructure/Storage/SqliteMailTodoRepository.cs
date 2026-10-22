using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>SQLite 待办仓储。</summary>
public sealed class SqliteMailTodoRepository : IMailTodoRepository
{
    private const string Columns = "id, email_id, title, due_utc, state, created_utc, completed_utc, reminded_utc";

    private readonly SqliteConnectionFactory _factory;

    public SqliteMailTodoRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<MailTodo>> QueryAsync(
        MailTodoState? state = null, bool overdueOnly = false, CancellationToken ct = default)
    {
        var conditions = new List<string>();
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (state is { } value)
        {
            conditions.Add("state = @state");
            command.Parameters.AddWithValue("@state", (int)value);
        }
        if (overdueOnly)
        {
            conditions.Add("state = 0 AND due_utc IS NOT NULL AND due_utc < @now");
            command.Parameters.AddWithValue("@now", SqlConvert.ToDb(DateTimeOffset.Now)!);
        }

        var where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        command.CommandText = $"SELECT {Columns} FROM mail_todos{where} ORDER BY CASE WHEN due_utc IS NULL THEN 1 ELSE 0 END, due_utc, id;";

        var todos = new List<MailTodo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) todos.Add(Map(reader));
        return todos;
    }

    public async Task<MailTodo?> GetByEmailAsync(long emailId, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM mail_todos WHERE email_id = @email ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("@email", emailId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<long> UpsertAsync(MailTodo todo, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (todo.Id == 0)
        {
            command.CommandText = """
INSERT INTO mail_todos(email_id, title, due_utc, state, created_utc)
VALUES(@email, @title, @due, @state, @created);
SELECT last_insert_rowid();
""";
            command.Parameters.AddWithValue("@created", SqlConvert.ToDb(todo.CreatedUtc)!);
        }
        else
        {
            command.CommandText = "UPDATE mail_todos SET title = @title, due_utc = @due, state = @state WHERE id = @id; SELECT @id;";
            command.Parameters.AddWithValue("@id", todo.Id);
        }

        command.Parameters.AddWithValue("@email", todo.EmailId);
        command.Parameters.AddWithValue("@title", todo.Title);
        command.Parameters.AddWithValue("@due", (object?)SqlConvert.ToDb(todo.DueUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("@state", (int)todo.State);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        todo.Id = id;
        return id;
    }

    public async Task SetStateAsync(long id, MailTodoState state, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mail_todos SET state = @state, completed_utc = @done WHERE id = @id;";
        command.Parameters.AddWithValue("@state", (int)state);
        command.Parameters.AddWithValue("@done", state == MailTodoState.Open
            ? DBNull.Value
            : (object?)SqlConvert.ToDb(DateTimeOffset.UtcNow)!);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mail_todos WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountOpenAsync(CancellationToken ct = default)
        => await CountAsync("SELECT COUNT(*) FROM mail_todos WHERE state = 0;", ct).ConfigureAwait(false);

    public async Task<int> CountOverdueAsync(CancellationToken ct = default)
        => await CountAsync(
            "SELECT COUNT(*) FROM mail_todos WHERE state = 0 AND due_utc IS NOT NULL AND due_utc < @now;",
            ct, DateTimeOffset.Now).ConfigureAwait(false);

    private async Task<int> CountAsync(string sql, CancellationToken ct, DateTimeOffset? now = null)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (now is { } value) command.Parameters.AddWithValue("@now", SqlConvert.ToDb(value)!);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    public async Task<IReadOnlyList<MailTodo>> GetDueForReminderAsync(
        DateTimeOffset now, int take = 20, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT {Columns} FROM mail_todos
WHERE state = 0 AND due_utc IS NOT NULL AND due_utc <= @now AND reminded_utc IS NULL
ORDER BY due_utc
LIMIT @take;
""";
        command.Parameters.AddWithValue("@now", SqlConvert.ToDb(now)!);
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 100));

        var todos = new List<MailTodo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) todos.Add(Map(reader));
        return todos;
    }

    public async Task SetRemindedAsync(long id, DateTimeOffset? remindedUtc, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mail_todos SET reminded_utc = @utc WHERE id = @id;";
        command.Parameters.AddWithValue("@utc", (object?)SqlConvert.ToDb(remindedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    private static MailTodo Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        EmailId = reader.GetInt64(1),
        Title = SqlConvert.Str(reader, 2),
        DueUtc = reader.IsDBNull(3) ? null : SqlConvert.ParseDate(reader.GetString(3)),
        State = (MailTodoState)reader.GetInt32(4),
        CreatedUtc = SqlConvert.ParseDate(reader.GetString(5)),
        CompletedUtc = reader.IsDBNull(6) ? null : SqlConvert.ParseDate(reader.GetString(6)),
        RemindedUtc = reader.IsDBNull(7) ? null : SqlConvert.ParseDate(reader.GetString(7))
    };
}
