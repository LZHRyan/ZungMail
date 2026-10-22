using CommunityToolkit.Mvvm.ComponentModel;

namespace ZungMail.App.ViewModels;

/// <summary>邮件头里的单个邮箱地址（逐地址交互：hover / 左键卡片 / 右键菜单）。</summary>
public sealed partial class MailAddressItem : ObservableObject
{
    public MailAddressItem(string address, string display, string group)
    {
        Address = address;
        Display = display;
        GroupLabel = group;
    }

    public string Address { get; }

    public string Display { get; }

    /// <summary>收件人 / 抄送 / 密送。</summary>
    public string GroupLabel { get; }

    [ObservableProperty]
    private bool _isCardOpen;
}
