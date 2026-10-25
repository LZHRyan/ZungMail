using ZungMail.Core.Abstractions;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// <c>meta</c> 表的键值读写。该表在建库脚本里就有（<c>schema_version</c> 等在用），
/// 这里只补一个通用的读写入口，供「一次性回填」这类跨启动状态使用。
/// </summary>
public sealed class SqliteMetadataStore : IAppMetadataStore
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMetadataStore(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task<bool> HasAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM meta WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) > 0;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO meta(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
""";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
