using System.Collections.Specialized;
using System.Windows;

namespace ZungMail.App.Services;

/// <summary>
/// 附件「复制」的剪贴板载荷：同时提供 <c>FileDrop</c>（资源管理器里可直接粘贴成文件）
/// 与纯文本路径（粘到聊天窗口、地址栏、记事本也能用），等价于在资源管理器里对文件按 Ctrl+C。
/// </summary>
public static class AttachmentClipboard
{
    public static DataObject BuildDataObject(string path)
    {
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        data.SetText(path);
        return data;
    }
}
