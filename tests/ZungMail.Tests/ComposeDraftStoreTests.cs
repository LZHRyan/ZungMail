using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 写信草稿的本地存储：保存 / 覆盖 / 删除 / 按更新时间排序 / 损坏文件不影响其它草稿。
/// </summary>
[Trait("Category", "Integration")]
public sealed class ComposeDraftStoreTests : IDisposable
{
    private readonly TempPaths _paths = new();
    private readonly JsonComposeDraftStore _store;

    public ComposeDraftStoreTests()
    {
        _store = new JsonComposeDraftStore(_paths);
    }

    public void Dispose() => _paths.Dispose();

    private string DraftsDirectory => Path.Combine(_paths.RootDirectory, "drafts");

    [Fact]
    public async Task SaveAndGet_一次往返保留全部字段()
    {
        var draft = new ComposeDraft
        {
            Mode = "New",
            AccountId = 7,
            To = "a@example.com",
            Cc = "b@example.com",
            Bcc = "c@example.com",
            Subject = "订舱确认 SO-123",
            BodyHtml = "<p>详见附件</p>",
            Attachments = new List<string> { @"C:\tmp\so.pdf" }
        };

        await _store.SaveAsync(draft);
        var loaded = await _store.GetAllAsync();

        var item = Assert.Single(loaded);
        Assert.Equal(draft.Id, item.Id);
        Assert.Equal("订舱确认 SO-123", item.Subject);
        Assert.Equal("a@example.com", item.To);
        Assert.Equal("c@example.com", item.Bcc);
        Assert.Equal("<p>详见附件</p>", item.BodyHtml);
        Assert.Single(item.Attachments);
    }

    [Fact]
    public async Task Save_相同Id覆盖而不是新增()
    {
        var draft = new ComposeDraft { Subject = "第一版" };
        await _store.SaveAsync(draft);

        draft.Subject = "第二版";
        await _store.SaveAsync(draft);

        var loaded = await _store.GetAllAsync();
        Assert.Single(loaded);
        Assert.Equal("第二版", loaded[0].Subject);
        Assert.Single(Directory.GetFiles(DraftsDirectory, "*.json"));
    }

    [Fact]
    public async Task GetAll_按更新时间倒序()
    {
        var older = new ComposeDraft { Subject = "旧" };
        await _store.SaveAsync(older);
        await Task.Delay(20);

        var newer = new ComposeDraft { Subject = "新" };
        await _store.SaveAsync(newer);

        var loaded = await _store.GetAllAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("新", loaded[0].Subject);
    }

    [Fact]
    public async Task Delete_删除后不再出现()
    {
        var draft = new ComposeDraft { Subject = "待删" };
        await _store.SaveAsync(draft);

        await _store.DeleteAsync(draft.Id);

        Assert.Empty(await _store.GetAllAsync());
    }

    [Fact]
    public async Task GetAll_损坏文件被跳过其它草稿照常读取()
    {
        var draft = new ComposeDraft { Subject = "正常" };
        await _store.SaveAsync(draft);
        await File.WriteAllTextAsync(Path.Combine(DraftsDirectory, "broken.json"), "{ 这不是 JSON");

        var loaded = await _store.GetAllAsync();

        var item = Assert.Single(loaded);
        Assert.Equal("正常", item.Subject);
    }

    [Fact]
    public async Task GetAll_目录不存在时返回空列表()
    {
        if (Directory.Exists(DraftsDirectory)) Directory.Delete(DraftsDirectory, true);

        Assert.Empty(await _store.GetAllAsync());
    }

    [Fact]
    public void HasContent_全空时为假其余为真()
    {
        Assert.False(new ComposeDraft().HasContent());
        Assert.False(new ComposeDraft { BodyHtml = "<p>&nbsp;</p>" }.HasContent());
        Assert.True(new ComposeDraft { Subject = "x" }.HasContent());
        Assert.True(new ComposeDraft { To = "a@b.com" }.HasContent());
        Assert.True(new ComposeDraft { BodyHtml = "<p>正文</p>" }.HasContent());
        Assert.True(new ComposeDraft { Attachments = new List<string> { "a.pdf" } }.HasContent());
    }

    private sealed class TempPaths : IAppPaths, IDisposable
    {
        public TempPaths()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "zungmail-drafts", Guid.NewGuid().ToString("N"));
        }

        public string RootDirectory { get; }
        public string DatabasePath => Path.Combine(RootDirectory, "mail.db");
        public string TemplatesPath => Path.Combine(RootDirectory, "templates.json");
        public string SettingsPath => Path.Combine(RootDirectory, "settings.json");
        public string LogDirectory => Path.Combine(RootDirectory, "logs");
        public string EmlDirectory => Path.Combine(RootDirectory, "eml");

        public void EnsureCreated() => Directory.CreateDirectory(RootDirectory);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDirectory)) Directory.Delete(RootDirectory, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
