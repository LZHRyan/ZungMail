using ZungMail.App.ViewModels;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>列表行徽标：货代功能关闭后，业务单号与异常角标都不再显示。</summary>
[Trait("Category", "Unit")]
public class MailRowFreightBadgeTests
{
    private static MailRow CreateRow(MailType type = MailType.Unknown) => new(new EmailSummary
    {
        Id = 1,
        Subject = "测试",
        FromEmail = "ops@forwarder.cn",
        IndustryType = type
    });

    [Fact]
    public void 关闭货代后异常角标不再显示()
    {
        var row = CreateRow(MailType.Rollover);
        Assert.True(row.ShowExceptionBadge); // 「甩柜」属于异常类型

        row.IndustryTypeVisible = false;
        Assert.False(row.ShowExceptionBadge);

        row.IndustryTypeVisible = true;
        Assert.True(row.ShowExceptionBadge);
    }

    [Fact]
    public void 非异常邮件不显示角标()
        => Assert.False(CreateRow(MailType.BookingConfirmation).ShowExceptionBadge);

    [Fact]
    public void 清空业务单号后列表徽标消失()
    {
        var row = CreateRow();
        Assert.False(row.HasReference);

        row.References = new[]
        {
            new FreightReference
            {
                Kind = FreightReferenceKind.BillOfLading,
                Value = "TS-SH-2610034",
                Normalized = "TSSH2610034"
            }
        };
        Assert.True(row.HasReference);
        Assert.Contains("TS-SH-2610034", row.ReferenceDisplay);

        row.References = Array.Empty<FreightReference>();
        Assert.False(row.HasReference);
        Assert.Equal("", row.ReferenceDisplay);
    }
}
