using ZungMail.Application.Services;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 历史邮件正文 base64 内嵌图的一次性回填：把 6.8MB 的正文压成引用，
/// 落盘文件登记为内嵌附件（导出 .eml / 转发内联都靠它），并用 meta 标记只跑一次。
/// </summary>
[Trait("Category", "Integration")]
public sealed class InlineDataImageBackfillServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly SqliteMetadataStore _meta;

    public InlineDataImageBackfillServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-backfill-" + Guid.NewGuid().ToString("N")[..8]);
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(_paths);
        new DatabaseInitializer(_factory).Initialize();
        _messages = new SqliteMessageRepository(_factory);
        _meta = new SqliteMetadataStore(_factory);
        SeedAccountAndFolder();
    }

    private void SeedAccountAndFolder()
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc) VALUES (1, '测试账户', 'me@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (1, 1, 'INBOX', '收件箱', 0);
""";
        command.ExecuteNonQuery();
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
        }
    }

    private async Task<long> AddMessageAsync(string bodyHtml)
    {
        var id = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 100,
            Subject = "水单 开票 SI VSLNB00637",
            FromEmail = "op02-meds@jihailog.com",
            DateUtc = new DateTimeOffset(2026, 10, 8, 3, 20, 28, TimeSpan.Zero),
            BodyState = BodyState.Downloaded
        });

        await _messages.SaveBodyAsync(id, bodyHtml, "提单麻烦催一下，是购汇水单", new List<AttachmentInfo>());
        return id;
    }

    private InlineDataImageBackfillService CreateService()
        => new(_messages, _paths, _meta, NullLogger<InlineDataImageBackfillService>.Instance);

    private static string BuildBigBody(int images, int imageBytes)
    {
        var png = new byte[imageBytes];
        for (var i = 0; i < png.Length; i++) png[i] = (byte)(i % 251);
        var base64 = Convert.ToBase64String(png);

        var sb = new System.Text.StringBuilder("<html><body><p>Dear,</p>");
        for (var i = 0; i < images; i++)
        {
            sb.Append("<img src=\"data:image/png;base64,").Append(base64).Append("\">");
        }

        sb.Append("<p>提单见附件</p></body></html>");
        return sb.ToString();
    }

    [Fact]
    public async Task 回填把内嵌图落盘并压缩正文()
    {
        var id = await AddMessageAsync(BuildBigBody(images: 20, imageBytes: 4096));
        var before = (await _messages.GetDetailAsync(id))!;
        var beforeLength = before.BodyHtml!.Length;

        var (processed, savedChars) = await CreateService().RunAsync();

        Assert.Equal(1, processed);
        Assert.True(savedChars > 0);

        var after = (await _messages.GetDetailAsync(id))!;
        Assert.True(after.BodyHtml!.Length < beforeLength / 20,
            $"正文应只剩引用：{after.BodyHtml.Length} vs {beforeLength}");
        Assert.DoesNotContain("base64", after.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file:///", after.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("提单见附件", after.BodyHtml);

        // 正文纯文本不受影响
        Assert.Contains("购汇水单", after.BodyText);

        // 20 张图登记为内嵌附件，且文件真的在磁盘上
        var inline = after.Attachments.Where(a => a.IsInline).ToList();
        Assert.Equal(20, inline.Count);
        foreach (var attachment in inline)
        {
            Assert.NotNull(attachment.LocalPath);
            Assert.True(File.Exists(attachment.LocalPath));
        }
    }

    [Fact]
    public async Task 回填只跑一次()
    {
        await AddMessageAsync(BuildBigBody(images: 2, imageBytes: 1024));

        var first = await CreateService().RunAsync();
        var second = await CreateService().RunAsync();

        Assert.Equal(1, first.Messages);
        Assert.Equal(0, second.Messages);
        Assert.True(await _meta.HasAsync(InlineDataImageBackfillService.MetaKey));
    }

    [Fact]
    public async Task 没有内嵌图的邮件不动()
    {
        var id = await AddMessageAsync("<p>普通正文，没有图片</p>");

        var (processed, _) = await CreateService().RunAsync();

        Assert.Equal(0, processed);
        var after = (await _messages.GetDetailAsync(id))!;
        Assert.Equal("<p>普通正文，没有图片</p>", after.BodyHtml);
        Assert.Empty(after.Attachments);
    }
}
