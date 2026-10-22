using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>SQLite 发件人黑白名单仓储。模式与名单类型共同唯一，重复添加只刷新备注。</summary>
public sealed class SqliteSenderFilterRepository : ISenderFilterRepository
{
    private const string Columns = "id, pattern, is_blacklist, note, created_utc";

    private readonly SqliteConnectionFactory _factory;

    public SqliteSenderFilterRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<SenderFilter>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM sender_filters ORDER BY is_blacklist DESC, pattern ASC;";

        var filters = new List<SenderFilter>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) filters.Add(Map(reader));
        return filters;
    }

    public async Task<long> UpsertAsync(SenderFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO sender_filters(pattern, is_blacklist, note, created_utc)
VALUES(@pattern, @blacklist, @note, @created)
ON CONFLICT(pattern, is_blacklist) DO UPDATE SET note = excluded.note;
SELECT id FROM sender_filters WHERE pattern = @pattern AND is_blacklist = @blacklist;
""";
        command.Parameters.AddWithValue("@pattern", filter.Pattern ?? string.Empty);
        command.Parameters.AddWithValue("@blacklist", filter.IsBlacklist ? 1 : 0);
        command.Parameters.AddWithValue("@note", filter.Note ?? string.Empty);
        command.Parameters.AddWithValue(
            "@created",
            SqlConvert.ToDb(filter.CreatedUtc) ?? SqlConvert.ToDb(DateTimeOffset.UtcNow)!);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        filter.Id = id;
        return id;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sender_filters WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static SenderFilter Map(SqliteDataReader reader) => new()
    {
        Id = SqlConvert.Long(reader, 0),
        Pattern = SqlConvert.Str(reader, 1),
        IsBlacklist = SqlConvert.Bool(reader, 2),
        Note = SqlConvert.Str(reader, 3),
        CreatedUtc = SqlConvert.ParseDateNullable(reader, 4) ?? DateTimeOffset.UtcNow
    };
}
