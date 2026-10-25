using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ZungMail.App.ViewModels;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 「AI 总结」界面契约：两处入口、卡片结构、设置卡、视图模型四态与降级提示。
/// 不创建真实窗口，也不依赖真实模型。
/// </summary>
[Trait("Category", "Ui")]
public sealed class MailSummaryUiTests : IDisposable
{
    private readonly string _root;

    public MailSummaryUiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-summary-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static string ViewPath(string name) => Path.Combine(AppContext.BaseDirectory, "Views", name);

    [Fact]
    public void 主窗口阅读工具条提供AI总结按钮与卡片()
    {
        var xaml = File.ReadAllText(ViewPath("MainWindow.xaml"));

        Assert.Contains("Command=\"{Binding SummarizeCommand}\"", xaml);
        Assert.Contains("IcoSummary", xaml);
        Assert.Contains("MailSummaryCard", xaml);
        Assert.Contains("DataContext=\"{Binding Summary}\"", xaml);
    }

    [Fact]
    public void 独立阅读窗口操作条同样提供入口与卡片()
    {
        var xaml = File.ReadAllText(ViewPath("MailReaderWindow.xaml"));

        Assert.Contains("Command=\"{Binding SummarizeCommand}\"", xaml);
        Assert.Contains("Text=\"AI 总结\"", xaml);
        Assert.Contains("MailSummaryCard", xaml);
    }

    [Fact]
    public void 摘要卡片包含三段结果与四个动作()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Controls", "MailSummaryCard.xaml"));

        Assert.Contains("Text=\"要点\"", xaml);
        Assert.Contains("Text=\"待处理\"", xaml);
        Assert.Contains("Text=\"单号\"", xaml);
        Assert.Contains("复制摘要", xaml);
        Assert.Contains("Command=\"{Binding RegenerateCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding SummarizeCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding CollapseCommand}\"", xaml);
        Assert.Contains("Binding NoticeText", xaml);

        // 摘要正文必须是可选中复制的只读文本（不是纯 TextBlock 列表）
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(xaml, "Style=\"{StaticResource SelectableText}\"").Count);
        Assert.Contains("Binding BulletsText", xaml);
        Assert.Contains("Binding ActionsText", xaml);
        Assert.Contains("Binding ReferencesText", xaml);
        Assert.DoesNotContain("ItemsControl ItemsSource", xaml);
    }

    [Fact]
    public void 设置页有独立的本地AI助手Tab与模型操作()
    {
        var xaml = File.ReadAllText(ViewPath("SettingsWindow.xaml"));

        // AI 设置已从「通用」拆成独立 Tab：标题、可见性绑定与内容都要在
        Assert.Contains("本地 AI 助手", xaml);
        Assert.Contains("IsAiSection", xaml);
        Assert.DoesNotContain("邮件摘要（AI 总结）", xaml);
        Assert.Contains("MailSummaryProviderOptions", xaml);
        Assert.Contains("MailSummaryLengthOptions", xaml);
        // 「在阅读区显示 AI 总结」勾选已去掉：卡片只由工具栏按钮触发；开关改成「启用本地 AI」
        Assert.DoesNotContain("在阅读区显示「AI 总结」", xaml);
        Assert.DoesNotContain("MailSummaryEnabled", xaml);
        Assert.Contains("Settings.LocalAiEnabled", xaml);
        Assert.Contains("启用本地 AI", xaml);
        Assert.Contains("DownloadMailSummaryModelCommand", xaml);
        Assert.Contains("RecheckMailSummaryModelCommand", xaml);
        Assert.Contains("OpenMailSummaryModelFolderCommand", xaml);
        Assert.Contains("DeleteMailSummaryModelCommand", xaml);
        Assert.Contains("Settings.MailSummaryModelUrl", xaml);
        // 隐私说明必须写在页面里
        Assert.Contains("隐私与资源占用", xaml);
    }

    [Fact]
    public async Task 视图模型_结果三段与耗时徽标()
    {
        var viewModel = CreateViewModel(new StubService
        {
            Result = Snapshot(
                new[] { "客户要订 1×40HC" },
                new[] { "10-25 18:00 前提交 SI" },
                new[] { "箱号 TCNU1234567" },
                fallback: null,
                truncated: false)
        });

        await viewModel.ShowAsync(Detail());

        Assert.True(viewModel.IsVisible);
        Assert.True(viewModel.HasResult);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsLoading);
        Assert.Equal("本地模型", viewModel.ProviderLabel);
        Assert.Equal("1.6s", viewModel.ElapsedText);
        Assert.Single(viewModel.Bullets);
        Assert.Contains("TCNU1234567", viewModel.References[0]);

        // 可选中复制的展示文本：每条一行、带「· 」前缀
        Assert.Contains("客户要订 1×40HC", viewModel.BulletsText);
        Assert.Contains("10-25 18:00 前提交 SI", viewModel.ActionsText);
        Assert.Contains("TCNU1234567", viewModel.ReferencesText);
        Assert.False(viewModel.HasNotice);
        Assert.Contains("待处理", viewModel.BuildCopyText());
    }

    [Fact]
    public async Task 视图模型_空段落显示未提及()
    {
        var viewModel = CreateViewModel(new StubService
        {
            Result = Snapshot(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, false)
        });

        await viewModel.ShowAsync(Detail());

        Assert.Equal(new[] { "未提及" }, viewModel.Bullets);
        Assert.Equal(new[] { "未提及" }, viewModel.Actions);
        Assert.Equal(new[] { "未提及" }, viewModel.References);
        Assert.Equal("· 未提及", viewModel.BulletsText);
        Assert.Equal("· 未提及", viewModel.ActionsText);
        Assert.Equal("· 未提及", viewModel.ReferencesText);
    }

    [Fact]
    public async Task 视图模型_降级与截断都有提示()
    {
        var viewModel = CreateViewModel(new StubService
        {
            Result = Snapshot(
                new[] { "要点" }, Array.Empty<string>(), Array.Empty<string>(),
                fallback: "本地模型尚未下载，已改用规则抽取",
                truncated: true)
        });

        await viewModel.ShowAsync(Detail());

        Assert.True(viewModel.HasNotice);
        Assert.Contains("尚未下载", viewModel.NoticeText);
        Assert.Contains("超出覆盖范围", viewModel.NoticeText);
        Assert.Equal("规则抽取", viewModel.ProviderLabel);
    }

    [Fact]
    public async Task 视图模型_失败时显示原因可重试()
    {
        var viewModel = CreateViewModel(new StubService
        {
            Failure = new MailSummaryUnavailableException("这封邮件没有可总结的正文。")
        });

        await viewModel.ShowAsync(Detail());

        Assert.True(viewModel.HasError);
        Assert.Contains("没有可总结的正文", viewModel.ErrorText);
        Assert.False(viewModel.HasResult);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task 视图模型_切换邮件清空卡片()
    {
        var viewModel = CreateViewModel(new StubService
        {
            Result = Snapshot(new[] { "要点" }, Array.Empty<string>(), Array.Empty<string>(), null, false)
        });

        await viewModel.ShowAsync(Detail());
        viewModel.Reset();

        Assert.False(viewModel.IsVisible);
        Assert.False(viewModel.HasResult);
        Assert.Empty(viewModel.Bullets);
    }

    [Fact]
    public async Task 卡片默认不显示_点总结按钮后才出现()
    {
        var viewModel = new MailSummaryViewModel(new StubService(), NullLogger<MailSummaryViewModel>.Instance);

        await viewModel.ShowAsync(Detail());

        // 没有「在阅读区显示 AI 总结」开关：显示与否只取决于用户点了没有
        Assert.True(viewModel.IsVisible);
        Assert.False(viewModel.HasError);
    }

    private MailSummaryViewModel CreateViewModel(StubService service)
        => new(service, NullLogger<MailSummaryViewModel>.Instance);

    private JsonSettingsStore CreateSettings()
    {
        var paths = new AppPaths(Path.Combine(_root, Guid.NewGuid().ToString("N")[..6]));
        paths.EnsureCreated();
        var store = new JsonSettingsStore(paths);
        store.LoadAsync().GetAwaiter().GetResult();
        store.Current.LocalAiEnabled = true;
        return store;
    }

    private static EmailDetail Detail() => new()
    {
        Id = 42,
        AccountId = 1,
        FolderId = 1,
        Subject = "订舱确认",
        FromEmail = "sender@example.com",
        BodyState = BodyState.Downloaded,
        BodyText = "请安排订舱"
    };

    private static MailSummaryResult Snapshot(
        IReadOnlyList<string> bullets,
        IReadOnlyList<string> actions,
        IReadOnlyList<string> references,
        string? fallback,
        bool truncated) => new()
    {
        MessageId = 42,
        ProviderId = fallback is null ? MailSummarySettings.LocalModelProviderId : MailSummarySettings.RuleBasedProviderId,
        ProviderName = fallback is null ? "本地模型" : "规则抽取",
        Bullets = bullets,
        Actions = actions,
        References = references,
        FallbackReason = fallback,
        Truncated = truncated,
        InputChars = 1200,
        ElapsedMs = 1600
    };

    private sealed class StubService : IMailSummaryService
    {
        public MailSummaryResult? Result { get; set; }

        public Exception? Failure { get; set; }

        public string PreferredProviderId => MailSummarySettings.AutoProviderId;

        public IReadOnlyList<MailSummaryProviderStatus> DescribeProviders()
            => Array.Empty<MailSummaryProviderStatus>();

        public MailSummaryResult? TryGetCached(long messageId) => null;

        public void ResetProviders()
        {
        }

        public Task<MailSummaryResult> SummarizeAsync(
            long messageId,
            bool force = false,
            IProgress<MailSummaryProgress>? progress = null,
            CancellationToken ct = default)
            => Failure is null
                ? Task.FromResult(Result ?? Snapshot(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, false))
                : Task.FromException<MailSummaryResult>(Failure);
    }
}
