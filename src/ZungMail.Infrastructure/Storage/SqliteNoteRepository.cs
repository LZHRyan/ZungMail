using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>记事本仓储（FR-11，P2）。</summary>
public sealed class SqliteNoteRepository : INoteRepository
{
    private const string Cols = "id, title, content, updated_utc";

    private readonly SqliteConnectionFactory _factory;

    public SqliteNoteRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Note>> GetAllAsync(string? filter = null, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(filter))
        {
            command.CommandText = $"SELECT {Cols} FROM notes ORDER BY updated_utc DESC;";
        }
        else
        {
            command.CommandText = $"SELECT {Cols} FROM notes WHERE title LIKE @f OR content LIKE @f ORDER BY updated_utc DESC;";
            command.Parameters.AddWithValue("@f", "%" + filter.Trim() + "%");
        }
        var result = new List<Note>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }
        return result;
    }

    public async Task<Note?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Cols} FROM notes WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<long> SaveAsync(Note note, CancellationToken ct = default)
    {
        note.UpdatedUtc = DateTimeOffset.UtcNow;
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (note.Id <= 0)
        {
            command.CommandText = """
INSERT INTO notes(title, content, updated_utc) VALUES(@title, @content, @updated);
SELECT last_insert_rowid();
""";
        }
        else
        {
            command.CommandText = """
UPDATE notes SET title = @title, content = @content, updated_utc = @updated WHERE id = @id;
SELECT @id;
""";
            command.Parameters.AddWithValue("@id", note.Id);
        }
        command.Parameters.AddWithValue("@title", note.Title ?? string.Empty);
        command.Parameters.AddWithValue("@content", note.Content ?? string.Empty);
        command.Parameters.AddWithValue("@updated", SqlConvert.ToDb(note.UpdatedUtc)!);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        note.Id = id;
        return id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notes WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Note Map(SqliteDataReader reader) => new()
    {
        Id = SqlConvert.Long(reader, 0),
        Title = SqlConvert.Str(reader, 1),
        Content = SqlConvert.Str(reader, 2),
        UpdatedUtc = SqlConvert.ParseDate(SqlConvert.StrOrNull(reader, 3))
    };
}
