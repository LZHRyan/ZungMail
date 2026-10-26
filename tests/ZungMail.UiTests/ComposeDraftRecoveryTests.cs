using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZungMail.App.Services;
using ZungMail.App.ViewModels;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;

namespace ZungMail.UiTests;

/// <summary>
/// 草稿恢复：打开写信窗口不再弹模态框，改成编辑区顶部一张卡片；
/// 不点就继续空白写，点「恢复到编辑器」才把内容填回来。
/// </summary>
[Trait("Category", "Ui")]
public sealed class ComposeDraftRecoveryTests
{
    [Fact]
    public void 写信窗口有草稿恢复卡片()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "ComposeWindow.xaml"));

        Assert.Contains("ShowDraftRecoveryCard", xaml);
        Assert.Contains("DraftRecoveryDescription", xaml);
        Assert.Contains("Content=\"恢复到编辑器\"", xaml);
        Assert.Contains("RestoreRecoveredDraftCommand", xaml);
        Assert.Contains("DismissDraftRecoveryCommand", xaml);
    }

    [Fact]
    public async Task 打开写信窗口只显示卡片_不弹模态框也不改正文()
    {
        var scene = new Scene();

        await scene.ViewModel.InitializeAsync();

        Assert.True(scene.ViewModel.ShowDraftRecoveryCard);
        Assert.Equal(0, scene.Dialogs.ConfirmCalls);
        Assert.Contains("上次草稿主题", scene.ViewModel.DraftRecoveryDescription);
        // 没点之前编辑器仍是空的，草稿内容不会悄悄覆盖
        Assert.Equal("", scene.ViewModel.Subject);
        Assert.Equal("", scene.ViewModel.To);
    }

    [Fact]
    public async Task 点恢复到编辑器才填回内容()
    {
        var scene = new Scene();
        await scene.ViewModel.InitializeAsync();

        scene.ViewModel.RestoreRecoveredDraftCommand.Execute(null);

        Assert.False(scene.ViewModel.ShowDraftRecoveryCard);
        Assert.Equal("上次草稿主题", scene.ViewModel.Subject);
        Assert.Equal("buyer@example.com", scene.ViewModel.To);
        Assert.Contains("已恢复", scene.ViewModel.StatusText);
    }

    [Fact]
    public async Task 收起提示不影响草稿本身()
    {
        var scene = new Scene();
        await scene.ViewModel.InitializeAsync();

        scene.ViewModel.DismissDraftRecoveryCommand.Execute(null);

        Assert.False(scene.ViewModel.ShowDraftRecoveryCard);
        Assert.Equal("", scene.ViewModel.Subject);
        // 草稿仍在存储里（下次打开写信窗口还会提示）
        Assert.Single(await scene.Drafts.GetAllAsync());
        Assert.Contains("草稿", scene.ViewModel.StatusText);
    }

    /// <summary>一套最小装配：空账户/模板/联系人，草稿库里有一份草稿，对话框只记调用次数。</summary>
    private sealed class Scene
    {
        public Scene()
        {
            Settings = new StubSettings();
            Drafts = new FakeDraftStore();
            Drafts.SaveAsync(new ComposeDraft
            {
                Id = "draft-1",
                To = "buyer@example.com",
                Subject = "上次草稿主题",
                BodyHtml = "<p>草稿正文</p>",
                UpdatedUtc = DateTimeOffset.Now.AddHours(-2)
            }).GetAwaiter().GetResult();

            Dialogs = new DialogSpy();

            ViewModel = new ComposeViewModel(
                accounts: EmptyProxy.Create<IAccountRepository>(),
                outbox: EmptyProxy.Create<IOutboxRepository>(),
                processor: EmptyProxy.Create<IOutboxProcessor>(),
                contacts: EmptyProxy.Create<IContactRepository>(),
                templates: EmptyProxy.Create<ITemplateStore>(),
                settings: Settings,
                dialogs: Dialogs.CreateProxy(),
                drafts: Drafts,
                draftMails: EmptyProxy.Create<IMailDraftService>(),
                theme: new ThemeManager(Settings),
                logger: NullLogger<ComposeViewModel>.Instance,
                request: new ComposeRequest());
        }

        public StubSettings Settings { get; }

        public FakeDraftStore Drafts { get; }

        public DialogSpy Dialogs { get; }

        public ComposeViewModel ViewModel { get; }
    }

    private sealed class StubSettings : ISettingsStore
    {
        public AppSettings Current { get; } = new();

        public string? LoadWarning => null;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeDraftStore : IComposeDraftStore
    {
        private readonly List<ComposeDraft> _drafts = new();

        public Task<IReadOnlyList<ComposeDraft>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ComposeDraft>>(_drafts.OrderByDescending(d => d.UpdatedUtc).ToList());

        public Task SaveAsync(ComposeDraft draft, CancellationToken ct = default)
        {
            _drafts.RemoveAll(d => d.Id == draft.Id);
            _drafts.Add(draft);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            _drafts.RemoveAll(d => d.Id == id);
            return Task.CompletedTask;
        }
    }

    /// <summary>记录有没有弹过模态确认框，其余调用一律「取消」。</summary>
    private class DialogSpy : DispatchProxy
    {
        public int ConfirmCalls { get; private set; }

        public IDialogService CreateProxy()
        {
            var proxy = Create<IDialogService, DialogSpy>();
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.Confirm)) ConfirmCalls++;
            if (targetMethod?.Name == nameof(IDialogService.ShowSettings)) return false;
            return targetMethod?.ReturnType == typeof(bool) ? false : null;
        }
    }

    /// <summary>空仓储替身：集合查询返回空列表，其余返回 null。</summary>
    private class EmptyProxy : DispatchProxy
    {
        public static T Create<T>() where T : class => DispatchProxy.Create<T, EmptyProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod!.ReturnType;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>)) return null;

            var resultType = returnType.GetGenericArguments()[0];
            if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                var element = resultType.GetGenericArguments()[0];
                var empty = Array.CreateInstance(element, 0);
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, new object?[] { empty });
            }

            return null;
        }
    }
}
