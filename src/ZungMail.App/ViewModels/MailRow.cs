using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZungMail.App.Services;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 邮件列表行。非会话模式下一行一封；会话模式下代表行为该会话最新一封，并携带会话封数（FR-05）。
/// </summary>
public sealed partial class MailRow : ObservableObject
{
    private readonly AvatarStore? _avatars;

    public MailRow(EmailSummary model, int threadCount = 1, AvatarStore? avatars = null)
    {
        Model = model;
        _threadCount = threadCount;
        _isUnread = model.IsUnread;
        _isStarred = model.IsStarred;
        _avatars = avatars;
    }

    public EmailSummary Model { get; }

    public long Id => Model.Id;

    public string Subject => Model.SubjectDisplay;

    public string From => Model.FromDisplay;

    public string Preview => Model.Preview;

    public string Date => Model.DateDisplay;

    public string Initial => Model.Initial;

    public string AvatarColor => Model.AvatarColor;

    /// <summary>发件人头像（Gravatar 命中才有）；为空时界面继续用首字母色块。</summary>
    public ImageBrush? AvatarBrush => _avatars?.GetBrush(Model.FromEmail);

    public bool HasAvatar => AvatarBrush is not null;

    /// <summary>头像异步加载完成后由主视图模型调用，刷新这一行的绑定。</summary>
    internal void RefreshAvatar()
    {
        OnPropertyChanged(nameof(AvatarBrush));
        OnPropertyChanged(nameof(HasAvatar));
    }

    public bool HasAttachment => Model.HasAttachment;

    /// <summary>行业邮件类型（订舱确认 / SI / 甩柜 …），列表只突出异常类型。</summary>
    public bool HasIndustryType => Model.HasIndustryType;

    public string IndustryTypeShort => Model.IndustryTypeShort;

    public string IndustryTypeLabel => Model.IndustryTypeLabel;

    public string IndustryTypeColor => Model.IndustryTypeColor;

    /// <summary>异常邮件（查验 / 甩柜 / 延误）在列表上用红色角标提示。</summary>
    public bool IsExceptionMail => Model.IsExceptionMail;

    /// <summary>设置里「货代 → 邮件类型」关闭后，列表不再显示异常角标。</summary>
    [ObservableProperty]
    private bool _industryTypeVisible = true;

    /// <summary>列表角标的可见性：既是异常邮件，且用户没有关掉邮件类型识别。</summary>
    public bool ShowExceptionBadge => IsExceptionMail && IndustryTypeVisible;

    partial void OnIndustryTypeVisibleChanged(bool value) => OnPropertyChanged(nameof(ShowExceptionBadge));

    /// <summary>陌生发件人：首次收到该地址来信（入库时的结论，之后不变）。</summary>
    public bool IsStranger => Model.IsStranger;

    /// <summary>设置里「标记陌生发件人」开关；关闭后角标立即消失但数据仍保留。</summary>
    [ObservableProperty]
    private bool _strangerBadgeEnabled = true;

    /// <summary>列表角标可见性：既是陌生发件人，且用户没有关掉这个提示。</summary>
    public bool StrangerBadgeVisible => IsStranger && StrangerBadgeEnabled;

    /// <summary>陌生发件人的说明文案，悬停提示用。</summary>
    public string StrangerHint => Model.StrangerHint;

    partial void OnStrangerBadgeEnabledChanged(bool value) => OnPropertyChanged(nameof(StrangerBadgeVisible));

    /// <summary>
    /// 仿冒域名（钓鱼）判定结论：呈现时由主视图模型算好塞进来，不落库；无风险为 null。
    /// </summary>
    [ObservableProperty]
    private PhishingDomainVerdict? _phishing;

    /// <summary>列表角标可见性：命中了仿冒域名。</summary>
    public bool PhishingBadgeVisible => Phishing is not null;

    /// <summary>悬停提示：与哪个自有域名相像、命中的原因。</summary>
    public string PhishingHint => Phishing?.Summary ?? "";

    partial void OnPhishingChanged(PhishingDomainVerdict? value)
    {
        OnPropertyChanged(nameof(PhishingBadgeVisible));
        OnPropertyChanged(nameof(PhishingHint));
    }

    public bool IsThread => ThreadCount > 1;

    public string ThreadCountDisplay => ThreadCount > 1 ? ThreadCount.ToString() : string.Empty;

    [ObservableProperty]
    private int _threadCount;

    [ObservableProperty]
    private bool _isUnread;

    [ObservableProperty]
    private bool _isStarred;

    /// <summary>当前页批量操作选择态；阅读窗格的选中行仍由 ListBox.SelectedItem 决定。</summary>
    [ObservableProperty]
    private bool _isBatchSelected;

    /// <summary>该邮件已打的标签，用于列表徽标展示。</summary>
    [ObservableProperty]
    private IReadOnlyList<MailTag> _tags = Array.Empty<MailTag>();

    public bool HasTags => Tags.Count > 0;

    partial void OnTagsChanged(IReadOnlyList<MailTag> value) => OnPropertyChanged(nameof(HasTags));

    /// <summary>该邮件识别出的货代业务单号（按业务优先级排序）。</summary>
    [ObservableProperty]
    private IReadOnlyList<FreightReference> _references = Array.Empty<FreightReference>();

    /// <summary>列表只展示优先级最高的一条单号（Job &gt; SO &gt; B/L &gt; 箱号 …）。</summary>
    public FreightReference? PrimaryReference => References.Count > 0 ? References[0] : null;

    public bool HasReference => References.Count > 0;

    public string ReferenceDisplay => PrimaryReference is null
        ? string.Empty
        : $"{PrimaryReference.KindShort} {PrimaryReference.Value}";

    public string ReferenceColor => PrimaryReference?.KindColor ?? "#64748b";

    public string ReferenceTooltip => PrimaryReference?.Display ?? string.Empty;

    partial void OnReferencesChanged(IReadOnlyList<FreightReference> value)
    {
        OnPropertyChanged(nameof(PrimaryReference));
        OnPropertyChanged(nameof(HasReference));
        OnPropertyChanged(nameof(ReferenceDisplay));
        OnPropertyChanged(nameof(ReferenceColor));
        OnPropertyChanged(nameof(ReferenceTooltip));
    }

    partial void OnThreadCountChanged(int value) => OnPropertyChanged(nameof(IsThread));
}
