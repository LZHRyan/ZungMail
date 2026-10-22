using System.Globalization;
using System.Linq;
using ZungMail.Core.Domain;

namespace ZungMail.App.ViewModels;

/// <summary>设置 → 账户 的列表摘要与选中状态联动（卡片 ↔ 账户对象）。</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>卡片选中即选中账户；命令、签名页与导入下拉仍使用 <see cref="Accounts"/>。</summary>
    public bool HasSelectedAccount => SelectedAccount is not null;

    /// <summary>需要访问账户且不在同步/诊断中的操作统一用它控制可用性。</summary>
    public bool CanRunAccountAction => HasSelectedAccount && !IsBusy;

    /// <summary>设为主账户：已经是主账户、账户已停用或正忙时不给点。</summary>
    public bool CanSetPrimaryAccount => SelectedAccount is { IsPrimary: false, IsEnabled: true } && !IsBusy;

    public bool HasNoAccounts => AccountCards.Count == 0;

    /// <summary>列表顶部摘要：账户数、启用数、本地邮件总量。</summary>
    public string AccountListSummary
    {
        get
        {
            if (AccountCards.Count == 0) return "尚未添加邮箱账户";

            var enabled = AccountCards.Count(card => card.IsEnabled);
            var totalMessages = AccountCards.Sum(card => (long)card.TotalMessages);
            var summary = $"共 {AccountCards.Count} 个账户 · 本地 {totalMessages.ToString("N0", CultureInfo.CurrentCulture)} 封";
            var disabled = AccountCards.Count - enabled;
            return disabled > 0 ? summary + $" · {disabled} 个已停用" : summary;
        }
    }

    partial void OnSelectedAccountChanged(Account? value)
    {
        var card = value is null
            ? null
            : AccountCards.FirstOrDefault(item => item.Id == value.Id);

        if (!ReferenceEquals(card, SelectedAccountCard)) SelectedAccountCard = card;
        NotifyAccountSelectionChanged();
    }

    partial void OnSelectedAccountCardChanged(AccountCardItem? value)
    {
        if (value is null)
        {
            if (SelectedAccount is not null) SelectedAccount = null;
        }
        else if (SelectedAccount?.Id != value.Id)
        {
            SelectedAccount = value.Account;
        }

        NotifyAccountSelectionChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunAccountAction));
        OnPropertyChanged(nameof(CanSetPrimaryAccount));
    }

    private void NotifyAccountSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedAccount));
        OnPropertyChanged(nameof(CanRunAccountAction));
        OnPropertyChanged(nameof(CanSetPrimaryAccount));
        OnPropertyChanged(nameof(AccountListSummary));
    }
}
