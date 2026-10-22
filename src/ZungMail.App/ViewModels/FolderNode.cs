using CommunityToolkit.Mvvm.ComponentModel;
using ZungMail.Core.Domain;

namespace ZungMail.App.ViewModels;

/// <summary>侧栏文件夹节点。</summary>
public sealed partial class FolderNode : ObservableObject
{
    public FolderNode(MailFolder model)
    {
        Model = model;
        _unreadCount = model.UnreadCount;
        _totalCount = model.TotalCount;
    }

    public MailFolder Model { get; }

    public long Id => Model.Id;

    /// <summary>服务器返回的英文系统文件夹名 → 中文显示名（与 Foxmail 一致，仅影响显示）。</summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INBOX"] = "收件箱",
        ["Drafts"] = "草稿箱",
        ["Sent Messages"] = "已发送",
        ["Sent"] = "已发送",
        ["Deleted Messages"] = "已删除",
        ["Trash"] = "已删除",
        ["Junk"] = "垃圾邮件",
        ["Spam"] = "垃圾邮件",
        ["Archive"] = "归档"
    };

    public string Title => KnownNames.TryGetValue(Model.DisplayName ?? string.Empty, out var localized)
        ? localized
        : Model.Title;

    public long AccountId => Model.AccountId;

    /// <summary>层级深度：按服务器实际分隔符计算，用于还原 Foxmail 式的文件夹缩进。</summary>
    public int Depth
    {
        get
        {
            var fullName = Model.FullName ?? string.Empty;
            // '/' 优先；只有不含 '/' 时才按 '.' 计层，避免把 “京东JD.com” 这类名字算成两层。
            return fullName.Contains('/')
                ? fullName.Count(character => character == '/')
                : fullName.Count(character => character == '.');
        }
    }

    /// <summary>列表缩进：每层 14px，避免子文件夹与父文件夹平级显示。</summary>
    public System.Windows.Thickness IndentMargin => new(14 + Depth * 14, 0, 12, 0);

    /// <summary>用户自建文件夹才允许重命名和删除。</summary>
    public bool IsUserFolder => Kind == FolderKind.None;

    /// <summary>可作为拖拽落点：可选、且不是发送箱/草稿箱这类不适合投递邮件的语义目录。</summary>
    public bool CanReceiveMailDrop =>
        Model.IsSelectable && Kind is not (FolderKind.Sent or FolderKind.Drafts);

    public FolderKind Kind => Model.Kind;

    public string IconKey => Kind switch
    {
        FolderKind.Inbox => "IcoInbox",
        FolderKind.Sent => "IcoSend",
        FolderKind.Drafts => "IcoDraft",
        FolderKind.Trash => "IcoTrash",
        FolderKind.Junk => "IcoJunk",
        FolderKind.Archive => "IcoArchive",
        _ => "IcoFolder"
    };

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>把数据库中的最新游标与计数同步回节点。</summary>
    public void Update(MailFolder folder)
    {
        Model.UidValidity = folder.UidValidity;
        Model.LastMaxUid = folder.LastMaxUid;
        Model.LastSyncUtc = folder.LastSyncUtc;
        UnreadCount = folder.UnreadCount;
        TotalCount = folder.TotalCount;
    }
}
