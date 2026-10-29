using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>箱型箱量徽标（`1×40HC`）：来自运输字段抽取，按页加载后填。</summary>
    [ObservableProperty]
    private string _boxesText = "";

    /// <summary>是否有箱型箱量可显示（空串表示这封没有）。</summary>
    public bool HasBoxes => BoxesText.Length > 0;

    partial void OnBoxesTextChanged(string value) => OnPropertyChanged(nameof(HasBoxes));

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

    /// <summary>退信（投递失败退回）：收信时判定，正文下载后补全失败收件人与状态码。</summary>
    public bool IsBounce => Model.IsBounce;

    /// <summary>设置里「标记退信邮件」开关；关闭后角标立即消失但数据仍保留。</summary>
    [ObservableProperty]
    private bool _bounceBadgeEnabled = true;

    /// <summary>列表角标可见性：既是退信，且用户没有关掉这个提示。</summary>
    public bool BounceBadgeVisible => IsBounce && BounceBadgeEnabled;

    /// <summary>退信角标的悬停提示（含失败收件人与状态码，有则显示）。</summary>
    public string BounceHint => Model.BounceHint;

    partial void OnBounceBadgeEnabledChanged(bool value) => OnPropertyChanged(nameof(BounceBadgeVisible));

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

    /// <summary>只有一个标签：行内直接用「实心标签色底 + 白字」的原胶囊样式。</summary>
    public bool HasSingleTag => Tags.Count == 1;

    /// <summary>多个标签：中性胶囊 + 每个名字用自己的标签色（单一底色盖不住多种颜色）。</summary>
    public bool HasMultipleTags => Tags.Count > 1;

    /// <summary>单标签时的名字与颜色（供实心胶囊使用）。</summary>
    public string SingleTagName => Tags.Count > 0 ? Tags[0].Name : "";

    public string SingleTagColor => Tags.Count > 0 ? Tags[0].Color : "#64748b";

    /// <summary>行内最多显示几个标签名；超出的折叠成 `…`（完整列表在整行悬停提示里）。</summary>
    private const int MaxInlineTags = 3;

    /// <summary>
    /// 行内标签的一个色段：名字 + 自身标签色 + 该段的圆角。
    /// 首段左端半圆、末段右端半圆（高 22 → 圆角 11），中间段是直角，拼起来就是一粒完整胶囊。
    /// </summary>
    public sealed record TagPiece(string Name, string Color, System.Windows.CornerRadius Corner);

    /// <summary>
    /// 行内标签色段：一粒胶囊按标签个数分段着色，每段用自己的标签色、白字，段与段之间靠颜色变化分隔。
    /// 超过 <see cref="MaxInlineTags"/> 个时追加一个灰色的 `…` 段。
    /// </summary>
    public IReadOnlyList<TagPiece> TagPieces { get; private set; } = Array.Empty<TagPiece>();

    /// <summary>摘要文本（不带颜色），供悬停提示与测试使用：`客户、紧急、待付款`。</summary>
    public string TagSummary => Tags.Count == 0
        ? ""
        : string.Join("、", Tags.Select(tag => tag.Name));

    private void RebuildTagPieces()
    {
        if (Tags.Count == 0)
        {
            TagPieces = Array.Empty<TagPiece>();
            return;
        }

        var shown = Tags.Take(MaxInlineTags).ToList();
        var total = shown.Count + (Tags.Count > MaxInlineTags ? 1 : 0);
        var pieces = new List<TagPiece>(total);

        for (var i = 0; i < shown.Count; i++)
        {
            pieces.Add(new TagPiece(shown[i].Name, shown[i].Color, CornerOf(i, total)));
        }

        if (Tags.Count > MaxInlineTags)
        {
            pieces.Add(new TagPiece("…", "#94a3b8", CornerOf(total - 1, total)));
        }

        TagPieces = pieces;
    }

    /// <summary>胶囊两端的半圆只给首段左角与末段右角，其余角为直角（半径 = 胶囊高度 26 的一半）。</summary>
    private static System.Windows.CornerRadius CornerOf(int index, int total)
    {
        const double radius = 13;
        var left = index == 0 ? radius : 0;
        var right = index == total - 1 ? radius : 0;
        return new System.Windows.CornerRadius(left, right, right, left);
    }

    partial void OnTagsChanged(IReadOnlyList<MailTag> value)
    {
        RebuildTagPieces();
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasSingleTag));
        OnPropertyChanged(nameof(HasMultipleTags));
        OnPropertyChanged(nameof(SingleTagName));
        OnPropertyChanged(nameof(SingleTagColor));
        OnPropertyChanged(nameof(TagPieces));
        OnPropertyChanged(nameof(TagSummary));
        OnPropertyChanged(nameof(RowTooltip));
    }

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
        OnPropertyChanged(nameof(RowTooltip));
    }

    /// <summary>
    /// 整行合并成一段悬停提示：原来行内挂了 4 个数据绑定 ToolTip，
    /// 一页 500 行就是 2000 个事件钩子；合并后只留容器上这一个，内容一次算好。
    /// </summary>
    public string RowTooltip
    {
        get
        {
            var lines = new List<string> { $"{From} <{Model.FromEmail}>  ·  {Date}" };

            if (ReferenceTooltip.Length > 0) lines.Add(ReferenceTooltip);
            if (BoxesText.Length > 0) lines.Add("箱型 " + BoxesText);
            if (HasTags) lines.Add("标签：" + string.Join("、", Tags.Select(tag => tag.Name)));
            if (HasIndustryType) lines.Add(Model.IndustryTypeHint);
            if (PhishingHint.Length > 0) lines.Add(PhishingHint);
            if (BounceHint.Length > 0) lines.Add(BounceHint);
            if (IsStranger) lines.Add(StrangerHint);

            return string.Join(Environment.NewLine, lines);
        }
    }

    partial void OnThreadCountChanged(int value) => OnPropertyChanged(nameof(IsThread));
}
