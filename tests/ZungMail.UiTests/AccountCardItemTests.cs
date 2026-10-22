using ZungMail.App.ViewModels;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>账户卡片的信息拼装：协议、登录方式、本地统计、状态与签名标记。</summary>
public sealed class AccountCardItemTests
{
    [Fact]
    public void FormatsEndpointsAndAuthKind()
    {
        var account = BuildAccount();
        var card = AccountCardItem.Create(account, 1789, 43, new AppSettings());

        Assert.Equal("IMAP imap.example.com:993 · SSL", card.ImapText);
        Assert.Equal("SMTP smtp.example.com:465 · SSL", card.SmtpText);
        Assert.Equal("授权码", card.AuthText);
        Assert.False(card.HasInsecureTransport);
    }

    [Fact]
    public void ShowsInsecureTransportWarning()
    {
        var account = BuildAccount();
        account.ImapSecure = SecureMode.StartTls;
        account.SmtpSecure = SecureMode.None;

        var card = AccountCardItem.Create(account, 0, 0, new AppSettings());

        Assert.Equal("IMAP imap.example.com:993 · STARTTLS", card.ImapText);
        Assert.Equal("SMTP smtp.example.com:465 · 不加密", card.SmtpText);
        Assert.True(card.HasInsecureTransport);
    }

    [Fact]
    public void MissingHostIsMarkedInsteadOfBlankPort()
    {
        var account = BuildAccount();
        account.ImapHost = "";

        var card = AccountCardItem.Create(account, 0, 0, new AppSettings());

        Assert.Equal("IMAP 未配置 · SSL", card.ImapText);
        Assert.False(card.HasCompleteConfig);
        Assert.Equal("配置不完整", card.StatusText);
    }

    [Fact]
    public void FormatsLocalStats()
    {
        var account = BuildAccount();

        var total = 1789.ToString("N0");
        var unread = 43.ToString("N0");

        Assert.Equal($"本地 {total} 封 · 未读 {unread}", AccountCardItem.Create(account, 1789, 43, new AppSettings()).LocalStatsText);
        Assert.Equal("本地 20 封 · 无未读", AccountCardItem.Create(account, 20, 0, new AppSettings()).LocalStatsText);
        Assert.Equal("本地暂无邮件", AccountCardItem.Create(account, 0, 0, new AppSettings()).LocalStatsText);
    }

    [Fact]
    public void SignatureStateFollowsAccountSettingThenLegacyFallback()
    {
        var account = BuildAccount();
        var settings = new AppSettings { Signature = "旧版全局签名" };
        Assert.True(AccountCardItem.Create(account, 0, 0, settings).SignatureEnabled);

        settings.Signatures.Add(new AccountSignature { AccountId = account.Id, IsEnabled = false, Html = "<p>签名</p>" });
        Assert.False(AccountCardItem.Create(account, 0, 0, settings).SignatureEnabled);

        settings.Signatures[0].IsEnabled = true;
        var enabled = AccountCardItem.Create(account, 0, 0, settings);
        Assert.True(enabled.SignatureEnabled);
        Assert.Equal("签名已启用", enabled.SignatureText);
    }

    [Fact]
    public void DisabledAccountShowsDisabledStatus()
    {
        var account = BuildAccount();
        account.IsEnabled = false;

        var card = AccountCardItem.Create(account, 0, 0, new AppSettings());

        Assert.False(card.IsEnabled);
        Assert.Equal("已停用", card.StatusText);
    }

    [Fact]
    public void SenderDisplayOnlyWhenDifferentFromAccountName()
    {
        var account = BuildAccount();
        Assert.False(AccountCardItem.Create(account, 0, 0, new AppSettings()).HasSenderDisplay);

        account.DisplayName = "王小明";
        var card = AccountCardItem.Create(account, 0, 0, new AppSettings());

        Assert.True(card.HasSenderDisplay);
        Assert.Equal("发件显示：王小明", card.SenderDisplayText);
    }

    [Fact]
    public void HidesDuplicateEmailAndShowsCustomLoginName()
    {
        var account = BuildAccount();
        account.Name = account.Email;
        var card = AccountCardItem.Create(account, 0, 0, new AppSettings());
        Assert.False(card.ShowEmailLine);
        Assert.False(card.HasCustomUsername);

        account.Username = "ops@corp.example.com";
        var custom = AccountCardItem.Create(account, 0, 0, new AppSettings());
        Assert.True(custom.HasCustomUsername);
        Assert.Equal("登录名 ops@corp.example.com", custom.UsernameText);
    }

    private static Account BuildAccount() => new()
    {
        Id = 7,
        Name = "工作邮箱",
        Email = "ops@example.com",
        Username = "ops@example.com",
        ImapHost = "imap.example.com",
        ImapPort = 993,
        ImapSecure = SecureMode.Ssl,
        SmtpHost = "smtp.example.com",
        SmtpPort = 465,
        SmtpSecure = SecureMode.Ssl,
        IsEnabled = true,
        Color = "#2563eb"
    };
}
