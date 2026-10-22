using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 改名（MailClient → ZungMail）后的旧数据目录接管：
/// 旧数据就在旧默认目录里就整目录改名搬过来（不复制），在自定义目录里就只把新指针指过去。
/// 全程只碰临时目录，绝不碰真实的 %APPDATA%。
/// </summary>
[Trait("Category", "Integration")]
public sealed class LegacyDataDirectoryMigrationTests : IDisposable
{
    private readonly string _root;

    public LegacyDataDirectoryMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-legacy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>新品牌默认目录（本测试里等价于 %APPDATA%\ZungMail）。</summary>
    private string NewDefault => Path.Combine(_root, "ZungMail");

    /// <summary>旧品牌默认目录（本测试里等价于 %APPDATA%\MailClient）。</summary>
    private string LegacyDefault => Path.Combine(_root, "MailClient");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DataDirectoryPointer CreatePointer() => new(NewDefault, LegacyDefault);

    // ---------------- 搬迁 / 接管 ----------------

    [Fact]
    public void Adopt_旧指针指向旧默认目录时整目录搬到新默认目录()
    {
        Directory.CreateDirectory(Path.Combine(LegacyDefault, "eml"));
        File.WriteAllText(Path.Combine(LegacyDefault, "mail.db"), "db-bytes");
        File.WriteAllText(Path.Combine(LegacyDefault, "eml", "1.eml"), "eml-bytes");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = LegacyDefault });

        var state = CreatePointer().EnsureLegacyAdopted();

        // 指针指向新目录，旧目录已经整体改名过去
        Assert.Equal(NewDefault, state.DataDirectory);
        Assert.Null(state.PendingSource);
        Assert.False(Directory.Exists(LegacyDefault));

        // 数据内容一字不变
        Assert.Equal("db-bytes", File.ReadAllText(Path.Combine(NewDefault, "mail.db")));
        Assert.Equal("eml-bytes", File.ReadAllText(Path.Combine(NewDefault, "eml", "1.eml")));

        // 新指针已经写好，下次启动走常规分支
        Assert.Equal(NewDefault, new DataDirectoryPointer(NewDefault).Read().DataDirectory);
    }

    [Fact]
    public void Adopt_没有旧指针但旧默认目录有数据时同样搬到新目录()
    {
        // 更老的版本可能从没写过指针，数据就在旧默认目录里
        Directory.CreateDirectory(LegacyDefault);
        File.WriteAllText(Path.Combine(LegacyDefault, "settings.json"), "{}");

        var state = CreatePointer().EnsureLegacyAdopted();

        Assert.Equal(NewDefault, state.DataDirectory);
        Assert.False(Directory.Exists(LegacyDefault));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(NewDefault, "settings.json")));
    }

    [Fact]
    public void Adopt_新默认目录是空目录时仍然搬迁()
    {
        Directory.CreateDirectory(NewDefault); // 空壳，没有任何用户数据
        Directory.CreateDirectory(LegacyDefault);
        File.WriteAllText(Path.Combine(LegacyDefault, "mail.db"), "db");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = LegacyDefault });

        var state = CreatePointer().EnsureLegacyAdopted();

        Assert.Equal(NewDefault, state.DataDirectory);
        Assert.Equal("db", File.ReadAllText(Path.Combine(NewDefault, "mail.db")));
        Assert.False(Directory.Exists(LegacyDefault));
    }

    [Fact]
    public void Adopt_旧指针指向自定义目录时只写新指针不搬文件()
    {
        var custom = Path.Combine(_root, "自定义数据");
        Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(custom, "mail.db"), "custom-db");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = custom });

        var state = CreatePointer().EnsureLegacyAdopted();

        // 原地接管：新指针直接指向用户当年选的自定义目录
        Assert.Equal(custom, state.DataDirectory);
        Assert.Equal("custom-db", File.ReadAllText(Path.Combine(custom, "mail.db")));
        Assert.False(File.Exists(Path.Combine(NewDefault, "mail.db")));
        Assert.Equal(custom, new DataDirectoryPointer(NewDefault).Read().DataDirectory);
    }

    [Fact]
    public void Adopt_待迁移源在接管时一并带过来()
    {
        var custom = Path.Combine(_root, "自定义数据");
        var target = Path.Combine(_root, "待迁移目标");
        Directory.CreateDirectory(custom);
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState
        {
            DataDirectory = custom,
            PendingSource = target
        });

        var state = CreatePointer().EnsureLegacyAdopted();

        Assert.Equal(custom, state.DataDirectory);
        Assert.Equal(target, state.PendingSource);
    }

    [Fact]
    public void Adopt_搬迁不动时会原地接管旧目录()
    {
        Directory.CreateDirectory(LegacyDefault);
        File.WriteAllText(Path.Combine(LegacyDefault, "mail.db"), "db");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = LegacyDefault });

        // 新默认目录已存在且非空：rename 一定失败，必须退回原地接管
        Directory.CreateDirectory(NewDefault);
        File.WriteAllText(Path.Combine(NewDefault, "settings.json"), "{}");

        var state = CreatePointer().EnsureLegacyAdopted();

        Assert.Equal(LegacyDefault, state.DataDirectory);
        Assert.True(File.Exists(Path.Combine(LegacyDefault, "mail.db")));
        Assert.False(File.Exists(Path.Combine(NewDefault, "mail.db")));

        // 新指针写在默认目录里，但指向旧路径
        Assert.Equal(LegacyDefault, new DataDirectoryPointer(NewDefault).Read().DataDirectory);
    }

    [Fact]
    public void Adopt_新指针已存在时完全不看旧目录()
    {
        var existing = Path.Combine(_root, "已经在用的目录");
        Directory.CreateDirectory(existing);
        new DataDirectoryPointer(NewDefault, LegacyDefault).Write(new DataDirectoryState { DataDirectory = existing });

        Directory.CreateDirectory(LegacyDefault);
        File.WriteAllText(Path.Combine(LegacyDefault, "mail.db"), "legacy");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = LegacyDefault });

        var state = CreatePointer().EnsureLegacyAdopted();

        Assert.Equal(existing, state.DataDirectory);
        // 旧目录原样保留，没有被搬动
        Assert.True(File.Exists(Path.Combine(LegacyDefault, "mail.db")));
        Assert.False(File.Exists(Path.Combine(existing, "mail.db")));
    }

    [Fact]
    public void Adopt_旧指针损坏时回退新默认目录且不抛异常()
    {
        Directory.CreateDirectory(LegacyDefault);
        File.WriteAllText(Path.Combine(LegacyDefault, "mail.db"), "legacy");
        File.WriteAllText(Path.Combine(LegacyDefault, "location.json"), "{ 这不是 JSON");

        var state = CreatePointer().EnsureLegacyAdopted();

        Assert.Equal(NewDefault, state.DataDirectory);
        Assert.Null(state.PendingSource);
        // 旧数据一个字节都没动
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(LegacyDefault, "mail.db")));
    }

    [Fact]
    public void Adopt_连续两次调用结果一致且只搬一次()
    {
        Directory.CreateDirectory(LegacyDefault);
        File.WriteAllText(Path.Combine(LegacyDefault, "mail.db"), "db");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = LegacyDefault });

        var first = CreatePointer().EnsureLegacyAdopted();
        var second = CreatePointer().EnsureLegacyAdopted();
        var third = CreatePointer().EnsureLegacyAdopted();

        Assert.Equal(NewDefault, first.DataDirectory);
        Assert.Equal(first.DataDirectory, second.DataDirectory);
        Assert.Equal(first.DataDirectory, third.DataDirectory);
        Assert.Equal("db", File.ReadAllText(Path.Combine(NewDefault, "mail.db")));
    }

    [Fact]
    public void Adopt_机器上既没有旧目录也没有旧指针时用新默认目录()
    {
        var state = CreatePointer().EnsureLegacyAdopted();

        Assert.Equal(NewDefault, state.DataDirectory);
        Assert.Null(state.PendingSource);
        Assert.False(Directory.Exists(LegacyDefault));
    }

    [Fact]
    public void Adopt_接管时会攒下可诊断说明()
    {
        Directory.CreateDirectory(LegacyDefault);
        File.WriteAllText(Path.Combine(LegacyDefault, "mail.db"), "db");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = LegacyDefault });

        var notices = new List<string>();
        void Collect(string line) => notices.Add(line);

        DataDirectoryPointer.LegacyTakeover += Collect;
        try
        {
            CreatePointer().EnsureLegacyAdopted();
        }
        finally
        {
            DataDirectoryPointer.LegacyTakeover -= Collect;
        }

        Assert.NotEmpty(notices);
        Assert.Contains(notices, line => line.Contains(LegacyDefault) && line.Contains(NewDefault));
    }

    [Fact]
    public void Adopt_整目录搬迁后库里的绝对路径改成新目录()
    {
        var emlDirectory = Path.Combine(LegacyDefault, "eml", "1");
        Directory.CreateDirectory(emlDirectory);
        var attachment = Path.Combine(emlDirectory, "报价.pdf");
        File.WriteAllText(attachment, "pdf");

        CreateDatabase(LegacyDefault, attachment, $"<p>正文</p><img src=\"{new Uri(attachment).AbsoluteUri}\"/>");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = LegacyDefault });

        var state = CreatePointer().EnsureLegacyAdopted();

        Assert.Equal(NewDefault, state.DataDirectory);

        // rename 之后库里的绝对路径也要跟着改，否则所有附件都会显示「未下载」、内嵌图片变占位
        var (localPath, bodyHtml) = ReadPaths(Path.Combine(NewDefault, "mail.db"));
        Assert.StartsWith(Path.Combine(NewDefault, "eml"), localPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(localPath));
        Assert.Contains(
            new Uri(Path.Combine(NewDefault, "eml", "1", "报价.pdf")).AbsoluteUri,
            bodyHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(new Uri(attachment).AbsoluteUri, bodyHtml, StringComparison.Ordinal);
    }

    // ---------------- 辅助 ----------------

    private static void CreateDatabase(string directory, string attachmentPath, string bodyHtml)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "mail.db"),
                Pooling = false
            }.ToString());
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, body_html TEXT, eml_path TEXT);
CREATE TABLE attachments (id INTEGER PRIMARY KEY AUTOINCREMENT, local_path TEXT);
""";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO messages (body_html, eml_path) VALUES (@html, @eml);";
            command.Parameters.AddWithValue("@html", bodyHtml);
            command.Parameters.AddWithValue("@eml", attachmentPath);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO attachments (local_path) VALUES (@path);";
            command.Parameters.AddWithValue("@path", attachmentPath);
            command.ExecuteNonQuery();
        }
    }

    private static (string LocalPath, string BodyHtml) ReadPaths(string databasePath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();

        string localPath;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT local_path FROM attachments LIMIT 1;";
            localPath = command.ExecuteScalar() as string ?? "";
        }

        string body;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT body_html FROM messages LIMIT 1;";
            body = command.ExecuteScalar() as string ?? "";
        }

        return (localPath, body);
    }

    [Fact]
    public void Adopt_不传旧目录时不做任何接管()
    {
        Directory.CreateDirectory(LegacyDefault);
        File.WriteAllText(Path.Combine(LegacyDefault, "mail.db"), "db");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = LegacyDefault });

        var state = new DataDirectoryPointer(NewDefault).EnsureLegacyAdopted();

        Assert.Equal(NewDefault, state.DataDirectory);
        Assert.True(File.Exists(Path.Combine(LegacyDefault, "mail.db")));
    }

    [Fact]
    public void Read_只读指针不搬动旧目录()
    {
        // 读指针是无副作用的：日志采集、启动环境快照、测试都会读它，
        // 一旦读操作顺手搬目录，跑一次测试就能把用户的真实数据搬走。
        Directory.CreateDirectory(LegacyDefault);
        File.WriteAllText(Path.Combine(LegacyDefault, "mail.db"), "db");
        new DataDirectoryPointer(LegacyDefault).Write(new DataDirectoryState { DataDirectory = LegacyDefault });

        var state = CreatePointer().Read();

        Assert.Equal(LegacyDefault, state.DataDirectory);
        Assert.True(File.Exists(Path.Combine(LegacyDefault, "mail.db")));
        Assert.False(File.Exists(Path.Combine(NewDefault, "location.json")));
    }
}
