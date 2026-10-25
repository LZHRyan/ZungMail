using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 侧边栏「往来邮件」卡片的数据：主题之外还要有正文摘要、带时分的日期、附件与未读标记。
/// 这些字段都来自同一次轻量投影查询，不为了好看多查一次详情。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MailContextCardTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMailContextRepository _context;

    /// <summary>昨天 21:42：用来断言「昨天 HH:mm」。</summary>
    private static readonly DateTimeOffset Yesterday =
        new(DateTime.Now.AddDays(-1).Date.AddHours(21).AddMinutes(42));

    /// <summary>更早的一封：用来断言「MM-dd HH:mm」（跨年时退化成完整日期，断言里兼容）。</summary>
    private static readonly DateTimeOffset Older =
        new(DateTime.Now.AddDays(-9).Date.AddHours(8).AddMinutes(30));

    public MailContextCardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-context-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _context = new SqliteMailContextRepository(_factory);
        Seed();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响验证结论
        }
    }

    private void Seed()
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc)
VALUES (1, '测试账户', 'me@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(id, account_id, full_name, display_name, kind)
VALUES (1, 1, 'INBOX', '收件箱', 0);

INSERT INTO messages(id, account_id, folder_id, uid, subject, from_name, from_email, date_utc,
                     preview, flags, size, has_attachment)
VALUES
 (10, 1, 1, 10, '当前这封', 'Carrier Ops', 'ops@carrier.example', @current, '', 1, 100, 0),
 (11, 1, 1, 11, '未读带附件', 'Carrier Ops', 'ops@carrier.example', @yesterday,
     '请查收订舱确认，SO No.: SHA1234567', 0, 200, 1),
 (12, 1, 1, 12, '已读无附件', 'Carrier Ops', 'ops@carrier.example', @older, '', 1, 300, 0);
""";
        command.Parameters.AddWithValue("@current", DateTimeOffset.Now.ToString("o"));
        command.Parameters.AddWithValue("@yesterday", Yesterday.ToString("o"));
        command.Parameters.AddWithValue("@older", Older.ToString("o"));
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task 往来邮件投影带上摘要与附件标记()
    {
        var items = await _context.GetCorrespondenceAsync(1, "ops@carrier.example", excludeMessageId: 10);

        Assert.Equal(2, items.Count); // 当前这封被排除
        var newest = items[0];
        Assert.Equal(11, newest.Id);
        Assert.True(newest.IsUnread);
        Assert.True(newest.HasAttachment);
        Assert.Equal("请查收订舱确认，SO No.: SHA1234567", newest.PreviewDisplay);
        Assert.True(newest.HasPreview);

        var older = items[1];
        Assert.Equal(12, older.Id);
        Assert.False(older.IsUnread);
        Assert.False(older.HasAttachment);
        Assert.False(older.HasPreview);
        Assert.Equal(string.Empty, older.PreviewDisplay);
    }

    [Fact]
    public async Task 卡片日期带时分_同一天也能分出先后()
    {
        var items = await _context.GetCorrespondenceAsync(1, "ops@carrier.example", excludeMessageId: 10);

        Assert.Equal("昨天 21:42", items[0].DateDetailDisplay);
        // 更早的一封：MM-dd HH:mm；若刚好跨年则会退化成完整日期
        Assert.Matches(@"^(\d{2}-\d{2} \d{2}:\d{2}|\d{4}-\d{2}-\d{2})$", items[1].DateDetailDisplay);
    }

    [Fact]
    public void 今天的相对时间只显示时分()
    {
        var today = DateTimeOffset.Now.Date.AddHours(9).AddMinutes(5);

        Assert.Equal("09:05", DisplayFormat.RelativeDateTime(today));
    }
}
