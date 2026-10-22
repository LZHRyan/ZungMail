using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 设置 → 关于 →「快捷方式」：安装器没建成桌面图标时的兜底（安装时的快捷方式写的是
/// <c>%PUBLIC%\Desktop</c>，部分机器这个目录被清理或被安全软件拦写，会弹「警告 1909」）。
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>快捷方式卡片的状态文案：已存在 / 未创建 / 上一次创建失败的原因。</summary>
    [ObservableProperty]
    private string _shortcutStatusText = "";

    /// <summary>进入关于页时刷新一次状态。</summary>
    public void RefreshShortcutState()
    {
        try
        {
            ShortcutStatusText = DesktopShortcutService.Exists()
                ? "桌面上已经有 ZungMail 快捷方式（点按钮可覆盖重建）。"
                : "桌面上还没有 ZungMail 快捷方式，点右侧按钮创建。";
        }
        catch (Exception ex)
        {
            ShortcutStatusText = "无法检查桌面路径：" + ex.Message;
        }
    }

    [RelayCommand]
    private void CreateDesktopShortcut()
    {
        var ok = DesktopShortcutService.TryCreate(out var path, out var error);
        RefreshShortcutState();

        if (ok)
        {
            _logger.LogInformation("已创建桌面快捷方式：{Path}", path);
            StatusText = "已在桌面创建 ZungMail 快捷方式";
            ShortcutStatusText = "已创建：" + path;
            return;
        }

        _logger.LogWarning("创建桌面快捷方式失败：{Error}", error);
        StatusText = "创建快捷方式失败：" + error;
        ShortcutStatusText = "创建失败：" + error + "（可以手动把 ZungMail.exe 拖到桌面，或从开始菜单右键「发送到 → 桌面快捷方式」）";
    }
}
