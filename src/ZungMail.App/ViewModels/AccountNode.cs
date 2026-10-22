using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.Core.Domain;

namespace ZungMail.App.ViewModels;

/// <summary>侧栏账户分组：标题 + 该账户的文件夹列表。</summary>
public sealed partial class AccountNode : ObservableObject
{
    private readonly AvatarStore? _avatars;

    public AccountNode(Account model, AvatarStore? avatars = null)
    {
        Model = model;
        _avatars = avatars;
    }

    public Account Model { get; private set; }

    public long Id => Model.Id;

    public string Title => string.IsNullOrWhiteSpace(Model.Name) ? Model.Email : Model.Name;

    /// <summary>账户是否处于正常使用状态（停止使用后不参与同步）。</summary>
    public bool IsEnabled => Model.IsEnabled;

    /// <summary>主账户：侧栏里排在前面，并带「主」标记。</summary>
    public bool IsPrimary => Model.IsPrimary;

    /// <summary>主账户角标文案（副账户为空，不占位）。</summary>
    public string PrimaryBadge => IsPrimary ? "主" : "";

    public string StatusSuffix => IsEnabled ? "" : "（已停止）";

    /// <summary>停用账户整体降透明度，提示其不参与同步。</summary>
    public double NodeOpacity => IsEnabled ? 1.0 : 0.55;

    /// <summary>能否对该账户发起收取：停用或配置不完整时不允许。</summary>
    public bool CanSync => IsEnabled && Model.HasCompleteConfig;

    /// <summary>「收取」被禁用时的悬停说明。</summary>
    public string SyncHint => !IsEnabled
        ? "账户已停止使用，先到设置里启用后再收取"
        : !Model.HasCompleteConfig
            ? "账户配置不完整，请先到设置里补全"
            : "立即收取该账户的新邮件";

    public string Email => Model.Email;

    public string Initials => Model.Initials;

    public string Color => Model.Color;

    /// <summary>账户头像（Gravatar 命中才有）；为空时界面继续用首字母色块。</summary>
    public ImageBrush? AvatarBrush => _avatars?.GetBrush(Model.Email);

    public bool HasAvatar => AvatarBrush is not null;

    /// <summary>头像异步加载完成后刷新侧栏这一行。</summary>
    internal void RefreshAvatar()
    {
        OnPropertyChanged(nameof(AvatarBrush));
        OnPropertyChanged(nameof(HasAvatar));
    }

    public ObservableCollection<FolderNode> Folders { get; } = new();

    public int UnreadCount => Folders.Sum(f => f.UnreadCount);

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>同步最新的账户设置（含启用状态），并刷新相关派生属性。</summary>
    public void Update(Account account)
    {
        Model = account;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(StatusSuffix));
        OnPropertyChanged(nameof(NodeOpacity));
        OnPropertyChanged(nameof(CanSync));
        OnPropertyChanged(nameof(SyncHint));
        OnPropertyChanged(nameof(IsPrimary));
        OnPropertyChanged(nameof(PrimaryBadge));
    }

    /// <summary>文件夹列表变化后刷新账户级未读合计。</summary>
    public void RefreshUnread() => OnPropertyChanged(nameof(UnreadCount));

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}
