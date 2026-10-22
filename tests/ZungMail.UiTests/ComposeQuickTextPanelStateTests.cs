using ZungMail.App.Services;
using ZungMail.App.ViewModels;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>写信窗口的「快速文本」侧栏开关要记住状态，下次打开沿用（不用每次手动打开）。</summary>
public sealed class ComposeQuickTextPanelStateTests
{
    [Fact]
    public void 侧栏状态_默认关闭打开过就记住()
    {
        var settings = new MemorySettingsStore();

        // 首次使用还是关闭，且构造时不需要回写设置
        var first = Create(settings);
        Assert.False(first.IsQuickTextPanelVisible);
        Assert.Equal(0, settings.SaveCount);

        first.IsQuickTextPanelVisible = true;
        Assert.True(settings.Current.ComposeQuickTextPanelVisible);
        Assert.Equal(1, settings.SaveCount);

        // 再写一封邮件：侧栏保持上次打开的状态
        var second = Create(settings);
        Assert.True(second.IsQuickTextPanelVisible);
        // 恢复状态本身不写盘，只有用户手动切换才写
        Assert.Equal(1, settings.SaveCount);

        second.IsQuickTextPanelVisible = false;
        Assert.False(settings.Current.ComposeQuickTextPanelVisible);
        Assert.Equal(2, settings.SaveCount);
        Assert.False(Create(settings).IsQuickTextPanelVisible);
    }

    private static ComposeViewModel Create(ISettingsStore settings) => new(
        accounts: null!,
        outbox: null!,
        processor: null!,
        contacts: null!,
        templates: null!,
        settings: settings,
        dialogs: null!,
        drafts: null!,
        draftMails: null!,
        theme: new ThemeManager(settings),
        logger: NullLogger<ComposeViewModel>.Instance,
        request: new ComposeRequest());

    /// <summary>内存设置存储：SaveAsync 同步完成，便于断言写盘次数。</summary>
    private sealed class MemorySettingsStore : ISettingsStore
    {
        public AppSettings Current { get; } = new();

        public string? LoadWarning => null;

        public int SaveCount { get; private set; }

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
