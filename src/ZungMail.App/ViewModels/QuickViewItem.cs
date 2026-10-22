using CommunityToolkit.Mvvm.ComponentModel;
using ZungMail.Core.Domain;

namespace ZungMail.App.ViewModels;

/// <summary>侧栏「快捷视图」条目：跨账户的统一入口（FR-04）。</summary>
public sealed partial class QuickViewItem : ObservableObject
{
    public QuickViewItem(string title, string iconKey, MailListFilter filter, FolderKind? scopeKind = null)
    {
        Title = title;
        IconKey = iconKey;
        Filter = filter;
        ScopeKind = scopeKind;
    }

    public string Title { get; }

    public string IconKey { get; }

    /// <summary>列表过滤维度（全部 / 未读 / 星标 / 附件）。</summary>
    public MailListFilter Filter { get; }

    /// <summary>限定到的语义文件夹类型；null 表示不限。</summary>
    public FolderKind? ScopeKind { get; }

    [ObservableProperty]
    private bool _isSelected;
}
