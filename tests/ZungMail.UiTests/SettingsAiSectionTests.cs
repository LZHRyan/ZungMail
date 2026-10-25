using System;
using System.IO;
using System.Linq;
using ZungMail.App.Services;
using ZungMail.App.ViewModels;
using ZungMail.Application.DependencyInjection;
using ZungMail.Core.Abstractions;
using ZungMail.Infrastructure.DependencyInjection;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail.DependencyInjection;
using ZungMail.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 设置中心的分区导航：「本地 AI 助手」必须是一个独立 Tab（不在「通用」里），
/// 选中它之后内容区切到 AI 页。用真实容器装配 SettingsViewModel，避免只验证 XAML 字符串。
/// </summary>
[Trait("Category", "Ui")]
public sealed class SettingsAiSectionTests : IDisposable
{
    private readonly string _root;
    private readonly ServiceProvider _provider;

    public SettingsAiSectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-settings-ui-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddZungMailInfrastructure();
        services.AddZungMailMail();
        services.AddZungMailApplication();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<ClientLockService>();
        services.AddSingleton<IDialogService, NullDialogService>();
        services.AddSingleton<AttachmentActionService>();
        services.AddSingleton<MailReceiveNotifier>();
        services.AddSingleton<AvatarStore>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<IStartupLaunchService, StartupLaunchService>();
        services.AddSingleton<UpdateCoordinator>();
        services.AddSingleton<UpdateInstallService>();
        services.AddSingleton<UpdateWindowService>();

        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task 本地AI助手是独立分区而不是通用里的卡片()
    {
        var settings = _provider.GetRequiredService<ISettingsStore>();
        await settings.LoadAsync();

        var viewModel = ActivatorUtilities.CreateInstance<SettingsViewModel>(_provider);

        // 1) 导航里有独立的「本地 AI 助手」，且排在「通用」之后
        var keys = viewModel.Sections.Select(section => section.Key).ToList();
        Assert.Contains("ai", keys);
        Assert.Equal("本地 AI 助手", viewModel.Sections.Single(section => section.Key == "ai").Title);
        Assert.True(keys.IndexOf("ai") > keys.IndexOf("general"));
        Assert.True(keys.IndexOf("ai") < keys.IndexOf("accounts"));

        // 2) 默认在「通用」，切换到 AI 后 IsAiSection 打开、IsGeneralSection 关闭
        viewModel.SelectedSection = viewModel.Sections.Single(section => section.Key == "general");
        Assert.True(viewModel.IsGeneralSection);
        Assert.False(viewModel.IsAiSection);

        viewModel.SelectedSection = viewModel.Sections.Single(section => section.Key == "ai");
        Assert.True(viewModel.IsAiSection);
        Assert.False(viewModel.IsGeneralSection);

        // 3) 模型状态与引擎选项在进入设置时已就绪（不需要等到点开模型卡片）
        Assert.False(string.IsNullOrWhiteSpace(viewModel.MailSummaryModelStatusText));
        Assert.Equal(3, viewModel.MailSummaryProviderOptions.Count);
        Assert.Equal(3, viewModel.MailSummaryLengthOptions.Count);
    }
}
