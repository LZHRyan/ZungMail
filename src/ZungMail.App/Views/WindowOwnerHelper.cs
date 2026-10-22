using System.Windows;

namespace ZungMail.App.Views;

/// <summary>对话框辅助：以主窗口为父级显示模态窗口。</summary>
public static class WindowOwnerHelper
{
    public static bool? ShowDialog(Window window)
    {
        if (System.Windows.Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }
        return window.ShowDialog();
    }
}
