using System.Windows.Media;
using ZungMail.App.Services;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 主窗口的头像展示：Gravatar 懒加载完成后把「首字母色块」换成真实头像。
/// 只在后台线程写入 <see cref="AvatarStore"/>，这里统一切回 UI 线程刷新绑定。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>阅读窗格发件人头像（Gravatar 命中才有）；为空时界面继续用首字母色块。</summary>
    public ImageBrush? DetailAvatarBrush => _avatars.GetBrush(Detail?.FromEmail);

    /// <summary>发件人是否有头像：无头像时显示首字母文字。</summary>
    public bool HasDetailAvatar => DetailAvatarBrush is not null;

    private void OnAvatarsChanged(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefreshAvatars);
            return;
        }

        RefreshAvatars();
    }

    /// <summary>
    /// 头像批量加载完成：刷新当前页每一行、侧栏账户与阅读窗格的头像。
    /// 只刷新派生属性，不重新查库——头像是最外层装饰，不能拖慢列表。
    /// </summary>
    private void RefreshAvatars()
    {
        foreach (var row in Messages) row.RefreshAvatar();
        foreach (var account in Accounts) account.RefreshAvatar();

        OnPropertyChanged(nameof(DetailAvatarBrush));
        OnPropertyChanged(nameof(HasDetailAvatar));
    }
}
