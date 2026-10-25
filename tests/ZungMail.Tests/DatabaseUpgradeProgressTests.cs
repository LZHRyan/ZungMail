using ZungMail.Infrastructure.Storage;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 老库升级的进度回报与快照读取。
///
/// 这条链路是「升级时不假死」的依据：<see cref="DatabaseInitializer.PeekSchemaVersion"/>
/// 让启动流程能提前判断要不要弹进度窗口，<see cref="DatabaseInitializer.Initialize"/>
/// 则要把每一步回报出来（机械盘上重建会话索引实测 163 秒）。
/// </summary>
[Trait("Category", "Integration")]
public sealed class DatabaseUpgradeProgressTests : IDisposable
{
    private readonly string _root;

    public DatabaseUpgradeProgressTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-progress-" + Guid.NewGuid().ToString("N")[..8]);
    }

    [Fact]
    public void PeekSchemaVersion_返回库里的版本且不建表()
    {
        var factory = CreateFactory();

        using (var connection = factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
INSERT INTO meta(key, value) VALUES('schema_version', '22');
""";
            command.ExecuteNonQuery();
        }

        var initializer = new DatabaseInitializer(factory);

        Assert.Equal(22, initializer.PeekSchemaVersion());

        using var verify = factory.Create();
        using var check = verify.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'messages';";
        Assert.Equal(0L, Convert.ToInt64(check.ExecuteScalar()));
    }

    [Fact]
    public void Initialize_老库升级时按步骤回报进度并升到最新版本()
    {
        var factory = CreateFactory();

        // 先用当前代码建出完整结构，再把版本改回 22 —— 模拟一台真实的旧库（结构齐全但版本落后），
        // 手工拼半个库会缺 message_ref_scan 这类迁移早期创建的对象，测不到真实路径。
        new DatabaseInitializer(factory).Initialize();
        using (var connection = factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE meta SET value = '22' WHERE key = 'schema_version';";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var progress = new RecordingProgress();
        new DatabaseInitializer(factory).Initialize(progress);

        // 结构准备 1 步 + v23/v24 两条迁移
        Assert.True(progress.Reports.Count >= 3, $"只收到 {progress.Reports.Count} 次回报");
        Assert.Equal(1, progress.Reports[0].Step);
        Assert.All(progress.Reports, r => Assert.Equal(progress.Reports.Count, r.Total));
        Assert.Contains(progress.Reports, r => r.Detail.Contains("退信标记"));
        Assert.Contains(progress.Reports, r => r.Detail.Contains("会话归并索引"));
        // 步骤必须单调递增，进度窗口才不会倒退
        for (var i = 1; i < progress.Reports.Count; i++)
        {
            Assert.True(progress.Reports[i].Step > progress.Reports[i - 1].Step);
        }

        using var verify = factory.Create();
        using (var version = verify.CreateCommand())
        {
            version.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            Assert.Equal(DatabaseInitializer.SchemaVersion.ToString(), Convert.ToString(version.ExecuteScalar()));
        }

        // 生成列与索引都要真的建出来
        using var column = verify.CreateCommand();
        column.CommandText = "SELECT COUNT(*) FROM pragma_table_xinfo('messages') WHERE name = 'thread_key';";
        Assert.Equal(1L, Convert.ToInt64(column.ExecuteScalar()));

        using var index = verify.CreateCommand();
        index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_messages_thread_key';";
        Assert.Equal(1L, Convert.ToInt64(index.ExecuteScalar()));
    }

    private SqliteConnectionFactory CreateFactory()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        return new SqliteConnectionFactory(paths);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响断言
        }
    }

    private sealed class RecordingProgress : IProgress<DatabaseUpgradeProgress>
    {
        public List<DatabaseUpgradeProgress> Reports { get; } = new();

        public void Report(DatabaseUpgradeProgress value) => Reports.Add(value);
    }
}
