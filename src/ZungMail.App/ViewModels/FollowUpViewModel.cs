using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.Application.Services;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 「等待回复」管理器：列进行中的跟进与每个人的回复情况。
/// 催信与打开邮件交给主窗口处理（这里只抛出请求），保证回复引用链与撰写逻辑单一来源。
/// </summary>
public sealed partial class FollowUpViewModel : ObservableObject
{
    private readonly MailFollowUpService _followUps;
    private readonly ILogger<FollowUpViewModel> _logger;

    public FollowUpViewModel(
        MailFollowUpService followUps,
        ILogger<FollowUpViewModel> logger)
    {
        _followUps = followUps;
        _logger = logger;
    }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>请求主窗口打开邮件或撰写催信。</summary>
    public event EventHandler<FollowUpActionRequest>? ActionRequested;

    public ObservableCollection<FollowUpStatus> Active { get; } = new();

    public ObservableCollection<FollowUpStatus> Finished { get; } = new();

    [ObservableProperty]
    private string _statusText = "正在加载…";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _isBusy;

    public bool HasActive => Active.Count > 0;

    public bool HasFinished => Finished.Count > 0;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasActive));

    public async Task InitializeAsync() => await ReloadAsync().ConfigureAwait(true);

    private async Task ReloadAsync()
    {
        try
        {
            IsBusy = true;
            var active = await _followUps.GetActiveAsync().ConfigureAwait(true);
            var finished = await _followUps.GetFinishedAsync(20).ConfigureAwait(true);

            Active.Clear();
            foreach (var item in active) Active.Add(item);
            Finished.Clear();
            foreach (var item in finished) Finished.Add(item);

            OnPropertyChanged(nameof(HasActive));
            OnPropertyChanged(nameof(HasFinished));

            Summary = active.Count == 0
                ? "没有等待回复的邮件"
                : $"{active.Count} 封邮件等待回复";
            StatusText = active.Count == 0
                ? "在邮件右键菜单选择「等待回复…」即可跟踪谁还没回"
                : "到点后会在托盘提醒一次；提醒后停在本轮，由你决定继续跟进或催信。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取等待回复列表失败");
            StatusText = "读取失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenMessage(FollowUpStatus? status)
    {
        if (status is null) return;
        ActionRequested?.Invoke(this, new FollowUpActionRequest(
            FollowUpActionKind.OpenMessage, status.FollowUp, status.Pending));
        CloseRequested?.Invoke(this, true);
    }

    /// <summary>给未回复的人写信：只带未回复的地址，正文沿用原邮件引用。</summary>
    [RelayCommand]
    private void Remind(FollowUpStatus? status)
    {
        if (status is null) return;
        if (status.Pending.Count == 0)
        {
            StatusText = "对方已全部回复，无需催信。";
            return;
        }

        ActionRequested?.Invoke(this, new FollowUpActionRequest(
            FollowUpActionKind.Remind, status.FollowUp, status.Pending));
        CloseRequested?.Invoke(this, true);
    }

    /// <summary>继续跟进：再等一个默认周期（4 小时）。</summary>
    [RelayCommand]
    private async Task ContinueAsync(FollowUpStatus? status)
    {
        if (status is null) return;

        try
        {
            var next = DateTimeOffset.Now.AddHours(4);
            await _followUps.RescheduleAsync(status.FollowUp.Id, next).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            StatusText = $"已继续跟进，下次检查：{Core.Domain.DisplayFormat.FullDate(next)}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "继续跟进失败：{Id}", status.FollowUp.Id);
            StatusText = "操作失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task CancelFollowUpAsync(FollowUpStatus? status)
    {
        if (status is null) return;

        try
        {
            await _followUps.CancelAsync(status.FollowUp.Id).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            StatusText = "已取消这条等待回复。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "取消等待回复失败：{Id}", status.FollowUp.Id);
            StatusText = "操作失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, true);
}
