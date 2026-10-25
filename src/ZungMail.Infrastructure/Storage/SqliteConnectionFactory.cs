using ZungMail.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// SQLite 连接工厂。每次打开的连接都会套用统一的 PRAGMA：
/// WAL 提升并发读写（NFR-R3）、外键级联、NORMAL 同步策略与忙等待超时。
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(IAppPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Cache = SqliteCacheMode.Shared,
            // 开连接池：仓储方法多、每个 1~3ms 的 open + 4 条 PRAGMA 会累加（首屏约 15~20 次调用）。
            // 导出 / 备份 / 数据目录迁移前由调用方 SqliteConnection.ClearAllPools() 释放文件句柄。
            Pooling = true
        }.ToString();
    }

    /// <summary>创建并打开连接，返回前已应用必要的 PRAGMA。</summary>
    public SqliteConnection Create()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    /// <summary>异步创建并打开连接。</summary>
    public async Task<SqliteConnection> CreateAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        ApplyPragmas(connection);
        return connection;
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // WAL 是持久化设置（会写入数据库头），此处重复设置以保证连接会话一致。
        command.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA foreign_keys=ON;" +
            "PRAGMA synchronous=NORMAL;" +
            "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
    }
}
