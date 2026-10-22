using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>发件箱（FR-08）：查看待发送 / 已发送 / 失败条目，支持立即投递、手动重试与删除。</summary>
public sealed partial class OutboxViewModel : ObservableObject
{
    private readonly IOutboxRepository _outbox;
    private readonly IOutboxProcessor _processor;
    private readonly IDialogService _dialogs;
    private readonly ILogger<OutboxViewModel> _logger;

    /// <summary>是否已订阅投递结果事件。</summary>
    private bool _attached;

    public OutboxViewModel(
        IOutboxRepository outbox,
        IOutboxProcessor processor,
        IDialogService dialogs,
        ILogger<OutboxViewModel> logger)
    {
        _outbox = outbox;
        _processor = processor;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<OutboxItem> Items { get; } = new();

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private OutboxItem? _selectedItem;

    [ObservableProperty] private string _statusText = "就绪";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isEmpty;

    public async Task InitializeAsync()
    {
        Attach();
        await ReloadAsync();
    }

    /// <summary>
    /// 订阅投递结果：后台调度器发出的邮件也能实时反映到列表，无需用户手动刷新。
    /// </summary>
    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _processor.ItemSent += OnItemChanged;
        _processor.ItemFailed += OnItemChanged;
    }

    /// <summary>窗口关闭时解除订阅，避免单例处理器长期持有已关闭窗口的视图模型。</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _processor.ItemSent -= OnItemChanged;
        _processor.ItemFailed -= OnItemChanged;
    }

    private void OnItemChanged(object? sender, OutboxItem item) => OnUi(() =>
    {
        if (IsBusy) return; // 正在手动投递，结束后会统一刷新
        _ = ReloadAsync();
    });

    [RelayCommand]
    private async Task ReloadAsync()
    {
        try
        {
            var items = await _outbox.GetAllAsync();
            var selectedId = SelectedItem?.Id;

            Items.Clear();
            foreach (var item in items) Items.Add(item);

            IsEmpty = Items.Count == 0;
            if (selectedId is { } id) SelectedItem = Items.FirstOrDefault(i => i.Id == id);

            var pending = Items.Count(i => i.State != OutboxState.Sent);
            var uncertain = Items.Count(i => i.State == OutboxState.Uncertain);
            StatusText = uncertain > 0
                ? $"共 {Items.Count} 条 · 待处理 {pending} 条 · 结果未知 {uncertain} 条"
                : $"共 {Items.Count} 条 · 待处理 {pending} 条";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取发件箱失败");
            StatusText = "读取失败：" + ex.Message;
        }
    }

    /// <summary>立即投递所有到期条目（含刚创建、时间已到的条目）。</summary>
    [RelayCommand]
    private async Task SendNowAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusText = "正在投递…";
        try
        {
            var sent = await _processor.ProcessDueAsync();
            await ReloadAsync();
            StatusText = sent > 0 ? $"已成功投递 {sent} 封" : "没有到期的待发送邮件";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "投递发件箱失败");
            StatusText = "投递失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 手动重试：把选中条目重置为待发送并立即尝试投递。
    /// 用于已用尽自动重试次数的失败邮件，以及程序中断残留的「发送中」条目。
    /// 「结果未知」条目会先要求确认，避免服务器已接受时重复投递。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRetrySelected))]
    private async Task RetryAsync()
    {
        if (SelectedItem is not { } item) return;

        if (item.State == OutboxState.Uncertain)
        {
            var confirm = _dialogs.Confirm(
                "发送结果未知",
                "该邮件的投递结果未知：服务器可能已经接受。",
                primaryText: "仍要重试",
                detail: "请先到邮箱的「已发送」中确认是否已发出，避免重复投递。",
                icon: DialogIcon.Warning,
                danger: true);
            if (!confirm) return;
        }

        IsBusy = true;
        StatusText = "正在重试…";
        try
        {
            await _outbox.UpdateStateAsync(item.Id, OutboxState.Pending, null, 0);
            await _processor.ProcessDueAsync();
            await ReloadAsync();

            var after = Items.FirstOrDefault(i => i.Id == item.Id);
            StatusText = after?.State switch
            {
                OutboxState.Sent => "重试成功：邮件已发送",
                OutboxState.Failed => "重试失败：" + after.LastError,
                OutboxState.Uncertain => "重试结果未知：" + after.LastError,
                _ => "已重新入队，到点后自动发送"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试发件箱条目失败：{Id}", item.Id);
            StatusText = "重试失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRetrySelected() => !IsBusy && SelectedItem is { CanRetry: true };

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedItem is not { } item) return;

        try
        {
            await _outbox.DeleteAsync(item.Id);
            Items.Remove(item);
            SelectedItem = null;
            IsEmpty = Items.Count == 0;
            StatusText = "已删除 1 条待发送记录";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除发件箱条目失败：{Id}", item.Id);
            StatusText = "删除失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, true);

    /// <summary>投递事件可能在后台线程触发，统一切回界面线程更新集合。</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
