using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.Application.Services;
using ZungMail.Core.Domain;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 「等待回复」创建窗口：勾选需要回复的联系人、选择判定条件与检查时间。
/// 不自动催信——这里只登记「等谁、什么时候看」，催信永远由人确认。
/// </summary>
public sealed partial class FollowUpEditViewModel : ObservableObject
{
    private readonly EmailDetail _detail;

    public FollowUpEditViewModel(EmailDetail detail, string selfEmail)
    {
        _detail = detail;

        var self = (selfEmail ?? string.Empty).Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var address in detail.To)
        {
            AddRecipient(address, isCc: false, self, seen);
        }
        foreach (var address in detail.Cc)
        {
            AddRecipient(address, isCc: true, self, seen);
        }

        if (Recipients.Count == 0)
        {
            // 没有收件人（例如自己给自己发的草稿）时至少允许对发件人跟进
            AddRecipient(new EmailAddress(detail.FromEmail, detail.FromName), isCc: false, self, seen);
        }

        Subject = detail.SubjectDisplay;
        IntervalOptions = MailFollowUpService.CheckIntervals.Select(item => item.Label).ToList();
        IntervalIndex = 1;
        MinReplies = Math.Min(2, Math.Max(Recipients.Count, 1));
    }

    public event EventHandler<bool>? CloseRequested;

    public string Subject { get; }

    public string FromDisplay => string.IsNullOrWhiteSpace(_detail.FromName)
        ? _detail.FromEmail
        : $"{_detail.FromName} <{_detail.FromEmail}>";

    public string SentDisplay => _detail.FullDateDisplay;

    public ObservableCollection<RecipientItem> Recipients { get; } = new();

    public IReadOnlyList<string> IntervalOptions { get; }

    [ObservableProperty]
    private int _intervalIndex = 1;

    /// <summary>true = 要求全部回复；false = 至少 N 人回复。</summary>
    [ObservableProperty]
    private bool _requireAll = true;

    [ObservableProperty]
    private int _minReplies = 1;

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private string _error = "";

    public int SelectedCount => Recipients.Count(item => item.IsSelected);

    public string SelectionText => $"已选 {SelectedCount}/{Recipients.Count} 人";

    public bool HasError => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    public FollowUpCreateRequest? BuildRequest()
    {
        var selected = Recipients.Where(item => item.IsSelected).Select(item => item.Address).ToList();
        if (selected.Count == 0)
        {
            Error = "请至少勾选一位需要回复的联系人。";
            return null;
        }

        var hours = MailFollowUpService.CheckIntervals[Math.Clamp(IntervalIndex, 0, IntervalOptions.Count - 1)].Hours;
        return new FollowUpCreateRequest(
            selected,
            RequireAll,
            RequireAll ? selected.Count : Math.Clamp(MinReplies, 1, selected.Count),
            Recipients.Any(item => item.IsSelected && item.IsCc),
            DateTimeOffset.Now.AddHours(hours),
            Note.Trim());
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Recipients) item.IsSelected = true;
        NotifySelection();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Recipients) item.IsSelected = false;
        NotifySelection();
    }

    [RelayCommand]
    private void Confirm()
    {
        if (BuildRequest() is null) return;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
    }

    private void AddRecipient(EmailAddress address, bool isCc, string self, HashSet<string> seen)
    {
        var value = (address.Address ?? string.Empty).Trim();
        if (value.Length == 0 || !value.Contains('@')) return;
        if (string.Equals(value, self, StringComparison.OrdinalIgnoreCase)) return;
        if (!seen.Add(value)) return;

        Recipients.Add(new RecipientItem
        {
            Address = value,
            Name = address.Name ?? string.Empty,
            IsCc = isCc,
            IsSelected = true
        });
    }

    /// <summary>可勾选的收件人。</summary>
    public sealed partial class RecipientItem : ObservableObject
    {
        public string Address { get; init; } = "";

        public string Name { get; init; } = "";

        public bool IsCc { get; init; }

        [ObservableProperty]
        private bool _isSelected = true;

        public string Display => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";

        public string KindLabel => IsCc ? "抄送" : "收件人";
    }
}
