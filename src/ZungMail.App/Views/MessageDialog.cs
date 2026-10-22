using System.Linq;
using System.Windows;

namespace ZungMail.App.Views;

/// <summary>自绘对话框左侧图标语义。</summary>
public enum DialogIcon
{
    /// <summary>不显示图标（纯文本提示）。</summary>
    None = 0,
    Info = 1,
    Success = 2,
    Warning = 3,
    Error = 4,
    Question = 5
}

/// <summary>确认对话框的三态结果。</summary>
public enum DialogChoice
{
    /// <summary>主操作（确定 / 删除 …）。</summary>
    Primary = 0,

    /// <summary>次要操作（如「仅停用」），没有次要按钮时不会返回。</summary>
    Secondary = 1,

    /// <summary>取消 / 关闭 / Esc。</summary>
    Cancel = 2
}

/// <summary>自绘对话框的显示参数。</summary>
public sealed class MessageDialogOptions
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    /// <summary>灰底块里的补充说明（副作用、不可撤销等），为空则不显示。</summary>
    public string? Detail { get; init; }

    public DialogIcon Icon { get; init; } = DialogIcon.Info;

    public string PrimaryText { get; init; } = "确定";

    /// <summary>次要按钮文案；为空表示没有次要按钮。</summary>
    public string? SecondaryText { get; init; }

    /// <summary>取消按钮文案；为空表示没有取消按钮（纯提示框）。</summary>
    public string? CancelText { get; init; } = "取消";

    /// <summary>主操作是否具有破坏性（删除 / 彻底清理），为真时主按钮使用告警色。</summary>
    public bool Danger { get; init; }
}

/// <summary>
/// 自绘对话框的统一入口：ViewModel 经 <c>IDialogService</c> 调用，视图代码可直接调用静态方法。
/// 父窗口缺省取当前激活窗口，保证对话框始终浮在触发它的窗口之上。
/// </summary>
public static class MessageDialog
{
    /// <summary>确认框：只有「主操作 / 取消」两个出口，返回是否选择了主操作。</summary>
    public static bool Confirm(
        string title,
        string message,
        string primaryText = "确定",
        string? detail = null,
        DialogIcon icon = DialogIcon.Question,
        bool danger = false,
        Window? owner = null)
        => Ask(new MessageDialogOptions
        {
            Title = title,
            Message = message,
            Detail = detail,
            Icon = icon,
            PrimaryText = primaryText,
            CancelText = "取消",
            Danger = danger
        }, owner) == DialogChoice.Primary;

    /// <summary>提示框：只有一个「确定」出口。</summary>
    public static void Alert(
        string title,
        string message,
        DialogIcon icon = DialogIcon.Info,
        string? detail = null,
        string okText = "知道了",
        Window? owner = null)
        => Ask(new MessageDialogOptions
        {
            Title = title,
            Message = message,
            Detail = detail,
            Icon = icon,
            PrimaryText = okText,
            CancelText = null
        }, owner);

    /// <summary>三态确认：主操作 / 次要操作 / 取消。</summary>
    public static DialogChoice Ask(MessageDialogOptions options, Window? owner = null)
    {
        var window = new MessageDialogWindow();
        window.Apply(options);

        var target = ResolveOwner(owner);
        if (target is not null)
        {
            window.Owner = target;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
        return window.Result;
    }

    /// <summary>挑一个可用父窗口：显式指定的优先，其次当前激活窗口，最后主窗口。</summary>
    private static Window? ResolveOwner(Window? owner)
    {
        if (owner is { IsLoaded: true }) return owner;

        var app = System.Windows.Application.Current;
        if (app is null) return null;

        return app.Windows
                   .OfType<Window>()
                   .FirstOrDefault(w => w.IsActive && w.IsLoaded && w.IsVisible)
               ?? (app.MainWindow is { IsLoaded: true } main ? main : null);
    }
}
