using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Configuration;
using Xunit;

namespace ZungMail.Tests;

/// <summary>设置 JSON 持久化：正常往返、损坏文件改名备份与默认值回退。</summary>
[Trait("Category", "Integration")]
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "zungmail-tests", Guid.NewGuid().ToString("N"));

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

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

    private FakePaths Paths => new(_root);

    private JsonSettingsStore CreateStore()
    {
        var paths = Paths;
        paths.EnsureCreated();
        return new JsonSettingsStore(paths);
    }

    [Fact]
    public async Task LoadAsync_文件损坏_改名备份并使用默认值()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(Paths.SettingsPath, "{ 这不是合法 JSON");

        await store.LoadAsync();

        // 以默认值启动
        Assert.Equal(new AppSettings().ListPageSize, store.Current.ListPageSize);
        Assert.True(store.Current.BlockRemoteImages);

        // 原文件已改名保留，且内容未被下一轮保存覆盖
        Assert.False(File.Exists(Paths.SettingsPath));
        var backup = Assert.Single(Directory.GetFiles(_root, "settings.corrupt.*.json"));
        Assert.Equal("{ 这不是合法 JSON", await File.ReadAllTextAsync(backup));

        Assert.NotNull(store.LoadWarning);
        Assert.Contains(Path.GetFileName(backup), store.LoadWarning);
    }

    [Fact]
    public async Task LoadAsync_正常文件_往返保留设置且无警告()
    {
        var store = CreateStore();
        store.Current.ListPageSize = 50;
        store.Current.Theme = "dark";
        store.Current.LaunchOnStartup = true;
        await store.SaveAsync();

        var reloaded = CreateStore();
        await reloaded.LoadAsync();

        Assert.Equal(50, reloaded.Current.ListPageSize);
        Assert.Equal("dark", reloaded.Current.Theme);
        Assert.True(reloaded.Current.LaunchOnStartup);
        Assert.Null(reloaded.LoadWarning);
        Assert.Empty(Directory.GetFiles(_root, "settings.corrupt.*.json"));
    }

    [Fact]
    public async Task LoadAsync_文件不存在_默认值且无警告()
    {
        var store = CreateStore();

        await store.LoadAsync();

        Assert.Equal(new AppSettings().ListPageSize, store.Current.ListPageSize);
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    public async Task LoadAsync_旧配置文件缺少货代字段_按全开处理()
    {
        // 升级前写出的 settings.json 没有 freight 相关字段，反序列化后必须保持「功能全开」
        var paths = Paths;
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath, """{ "theme": "dark", "listPageSize": 50 }""");

        var store = CreateStore();
        await store.LoadAsync();

        Assert.True(store.Current.FreightFeaturesEnabled);
        Assert.True(store.Current.FreightReferencesEnabled);
        Assert.True(store.Current.FreightMailTypesEnabled);
        Assert.True(store.Current.FreightDeadlinesEnabled);
        Assert.True(store.Current.FreightFollowUpsEnabled);
    }

    [Fact]
    public async Task 货代开关_往返保留()
    {
        var store = CreateStore();
        store.Current.FreightFeaturesEnabled = true;
        store.Current.FreightReferencesEnabled = false;
        store.Current.FreightDeadlinesEnabled = false;
        await store.SaveAsync();

        var reloaded = CreateStore();
        await reloaded.LoadAsync();

        Assert.True(reloaded.Current.FreightFeaturesEnabled);
        Assert.False(reloaded.Current.FreightReferencesEnabled);
        Assert.True(reloaded.Current.FreightMailTypesEnabled);
        Assert.False(reloaded.Current.FreightDeadlinesEnabled);
        Assert.True(reloaded.Current.FreightFollowUpsEnabled);
    }

    [Fact]
    public async Task 写信快速文本侧栏状态_往返保留()
    {
        var store = CreateStore();
        // 首次使用默认关闭，用户打开过一次后就应该记住
        Assert.False(store.Current.ComposeQuickTextPanelVisible);

        store.Current.ComposeQuickTextPanelVisible = true;
        await store.SaveAsync();

        var reloaded = CreateStore();
        await reloaded.LoadAsync();

        Assert.True(reloaded.Current.ComposeQuickTextPanelVisible);
    }

    [Fact]
    public async Task 长邮件默认折叠历史引用_默认开启且往返保留()
    {
        Assert.True(new AppSettings().FoldQuotedHistory);
        Assert.Equal(5, new AppSettings().FoldQuotedHistoryKeepLayers);

        var store = CreateStore();
        store.Current.FoldQuotedHistory = false;
        store.Current.FoldQuotedHistoryKeepLayers = 8;
        await store.SaveAsync();

        var reloaded = CreateStore();
        await reloaded.LoadAsync();

        Assert.False(reloaded.Current.FoldQuotedHistory);
        Assert.Equal(8, reloaded.Current.FoldQuotedHistoryKeepLayers);
    }

    /// <summary>只提供设置 / 模板路径的最小 IAppPaths 实现。</summary>
    private sealed class FakePaths : IAppPaths
    {
        public FakePaths(string root) => RootDirectory = root;

        public string RootDirectory { get; }
        public string DatabasePath => Path.Combine(RootDirectory, "mail.db");
        public string TemplatesPath => Path.Combine(RootDirectory, "templates.json");
        public string SettingsPath => Path.Combine(RootDirectory, "settings.json");
        public string LogDirectory => Path.Combine(RootDirectory, "logs");
        public string EmlDirectory => Path.Combine(RootDirectory, "eml");

        public void EnsureCreated() => Directory.CreateDirectory(RootDirectory);
    }
}
