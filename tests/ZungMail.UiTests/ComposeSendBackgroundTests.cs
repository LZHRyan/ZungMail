using System.IO;
using System.Reflection;
using ZungMail.App.Services;
using ZungMail.App.ViewModels;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 发送邮件不再等 SMTP：点「发送」→ 立刻入发件箱、关窗口，投递交给发件箱的后台线程。
/// 以前这里 <c>await _processor.ProcessDueAsync()</c> 要等信真的发出去才关窗，
/// 带附件的邮件几秒到几十秒关不掉，用户以为卡死。
/// </summary>
public sealed class ComposeSendBackgroundTests
{
    [Fact]
    public async Task 点发送后立刻关窗_不等后台投递完成()
    {
        var env = new Env();
        var vm = env.CreateViewModel();
        var closed = false;
        vm.CloseRequested += (_, ok) => closed = ok;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(closed, "点发送后应立即关闭写邮件窗口");
        Assert.Equal(1, env.Outbox.AddCalls);
        Assert.Null(env.Outbox.LastAdded!.ScheduledUtc);
        Assert.True(env.Processor.Started, "应立即踢一脚后台投递，不必干等 20 秒的调度轮询");
        Assert.False(env.Processor.ProcessTask.IsCompleted, "不应该等后台投递跑完才关窗");
        Assert.Contains("已加入发件箱", vm.StatusText);
    }

    [Fact]
    public async Task 定时发送交给调度器_点发送时不去投递()
    {
        var env = new Env();
        var vm = env.CreateViewModel();
        vm.ScheduleEnabled = true;
        vm.ScheduleDate = DateTime.Today.AddDays(1);
        vm.ScheduleTime = "09:00";
        var closed = false;
        vm.CloseRequested += (_, ok) => closed = ok;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.NotNull(env.Outbox.LastAdded!.ScheduledUtc);
        Assert.False(env.Processor.Started, "定时邮件按到点时间由调度器投递，不在点发送时发");
    }

    [Fact]
    public async Task 连发件箱都写不进去时留在窗口里报错()
    {
        var env = new Env { FailAdd = true };
        var vm = env.CreateViewModel();
        var closed = false;
        vm.CloseRequested += (_, ok) => closed = ok;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Contains("无法加入发件箱", vm.StatusText);
        Assert.False(env.Processor.Started);
    }

    [Fact]
    public void 发送按钮写明入发件箱后关窗()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "ComposeWindow.xaml"));

        Assert.Contains("Content=\"发送\"", xaml);
        Assert.Contains("ToolTip=\"发送到发件箱后立刻关窗", xaml);
        Assert.Contains("Click=\"OnSendClick\"", xaml);
    }

    /// <summary>测试用的组合：写邮件窗口只依赖账户、发件箱与处理器，其余给空实现。</summary>
    private sealed class Env
    {
        public Env()
        {
            Settings = new StubSettings();
            Outbox = new FakeOutboxRepository();
            Processor = new FakeOutboxProcessor();
        }

        public StubSettings Settings { get; }

        public FakeOutboxRepository Outbox { get; }

        public FakeOutboxProcessor Processor { get; }

        public bool FailAdd { get; init; }

        public ComposeViewModel CreateViewModel()
        {
            Outbox.FailAdd = FailAdd;

            var account = new Account
            {
                Id = 1,
                Name = "测试账户",
                Email = "me@example.com",
                Username = "me@example.com",
                ImapHost = "imap.example.com",
                SmtpHost = "smtp.example.com"
            };

            var vm = new ComposeViewModel(
                accounts: null!,
                outbox: Outbox,
                processor: Processor,
                contacts: SuggestProxy.Create(),
                templates: null!,
                settings: Settings,
                dialogs: null!,
                drafts: null!,
                draftMails: null!,
                theme: new ThemeManager(Settings),
                logger: NullLogger<ComposeViewModel>.Instance,
                request: new ComposeRequest());

            vm.SelectedAccount = account;
            vm.To = "buyer@example.com";
            vm.Subject = "订舱确认 TS-NB-2608206";
            vm.BodyHtml = "<p>请查收订舱确认。</p>";
            return vm;
        }
    }

    private sealed class FakeOutboxRepository : IOutboxRepository
    {
        public bool FailAdd { get; set; }

        public int AddCalls { get; private set; }

        public OutboxItem? LastAdded { get; private set; }

        public Task<long> AddAsync(OutboxItem item, CancellationToken ct = default)
        {
            AddCalls++;
            LastAdded = item;
            return FailAdd
                ? Task.FromException<long>(new IOException("磁盘已满"))
                : Task.FromResult(1L);
        }

        public Task<IReadOnlyList<OutboxItem>> GetDueAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutboxItem>>(Array.Empty<OutboxItem>());

        public Task<IReadOnlyList<OutboxItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutboxItem>>(Array.Empty<OutboxItem>());

        public Task<OutboxItem?> GetAsync(long id, CancellationToken ct = default)
            => Task.FromResult<OutboxItem?>(LastAdded);

        public Task UpdateStateAsync(long id, OutboxState state, string? error, int attempts,
            DateTimeOffset? nextAttemptUtc = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>模拟「信正在后台发」：ProcessDueAsync 挂住不返回，用来证明窗口没有等它。</summary>
    private sealed class FakeOutboxProcessor : IOutboxProcessor
    {
        private readonly TaskCompletionSource<int> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<OutboxItem>? ItemSent;

        public event EventHandler<OutboxItem>? ItemFailed;

        public bool Started { get; private set; }

        public Task<int> ProcessTask => _tcs.Task;

        public Task<int> ProcessDueAsync(CancellationToken ct = default)
        {
            Started = true;
            return _tcs.Task;
        }

        public Task<int> RecoverInterruptedAsync(CancellationToken ct = default) => Task.FromResult(0);

        public void RaiseSent(OutboxItem item) => ItemSent?.Invoke(this, item);

        public void RaiseFailed(OutboxItem item) => ItemFailed?.Invoke(this, item);
    }

    private sealed class StubSettings : ISettingsStore
    {
        public AppSettings Current { get; } = new();

        public string? LoadWarning => null;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>联系人补全：返回空列表（写邮件窗口输入收件人时会防抖查一次）。</summary>
    private class SuggestProxy : DispatchProxy
    {
        public static IContactRepository Create()
        {
            var proxy = Create<IContactRepository, SuggestProxy>();
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod!.ReturnType;
            if (returnType == typeof(Task<IReadOnlyList<Contact>>))
            {
                return Task.FromResult<IReadOnlyList<Contact>>(Array.Empty<Contact>());
            }

            return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
                ? typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(returnType.GetGenericArguments()[0])
                    .Invoke(null, new object?[] { null })
                : null;
        }
    }
}
