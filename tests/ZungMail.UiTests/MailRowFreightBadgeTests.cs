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
    public void 只有一个标签时沿用实心胶囊()
    {
        var row = CreateRow();
        row.Tags = new[] { new MailTag { Id = 1, Name = "客户", Color = "#2563eb" } };

        Assert.True(row.HasSingleTag);
        Assert.False(row.HasMultipleTags);
        Assert.Equal("客户", row.SingleTagName);
        Assert.Equal("#2563eb", row.SingleTagColor);
    }

    [Fact]
    public void 多个标签时改用中性胶囊()
    {
        var row = CreateRow();
        row.Tags = new[]
        {
            new MailTag { Id = 1, Name = "账单", Color = "#d97706" },
            new MailTag { Id = 2, Name = "JH", Color = "#2563eb" }
        };

        Assert.False(row.HasSingleTag);
        Assert.True(row.HasMultipleTags);
    }

    [Fact]
    public void 行内标签胶囊按顺序列出标签并各自带色()
    {
        var row = CreateRow();
        row.Tags = new[]
        {
            new MailTag { Id = 1, Name = "客户", Color = "#2563eb" },
            new MailTag { Id = 2, Name = "紧急", Color = "#dc2626" },
            new MailTag { Id = 3, Name = "待付款", Color = "#d97706" }
        };

        // 一个胶囊里按顺序排开，每个片段带自己的标签色，分隔符只在中间
        Assert.Equal(3, row.TagPieces.Count);
        Assert.Equal(new[] { "客户", "紧急", "待付款" }, row.TagPieces.Select(piece => piece.Name));
        Assert.Equal(new[] { "#2563eb", "#dc2626", "#d97706" }, row.TagPieces.Select(piece => piece.Color));
        // 首段左端半圆、末段右端半圆，中间段直角：拼起来是一粒完整胶囊
        Assert.Equal(13, row.TagPieces[0].Corner.BottomLeft);
        Assert.Equal(0, row.TagPieces[0].Corner.BottomRight);
        Assert.Equal(13, row.TagPieces[^1].Corner.BottomRight);

        Assert.Equal("客户、紧急、待付款", row.TagSummary);
        Assert.Contains("标签：客户、紧急、待付款", row.RowTooltip);
    }

    [Fact]
    public void 超过三个标签折叠成省略号()
    {
        var row = CreateRow();
        row.Tags = new[]
        {
            new MailTag { Id = 1, Name = "A" }, new MailTag { Id = 2, Name = "B" },
            new MailTag { Id = 3, Name = "C" }, new MailTag { Id = 4, Name = "D" },
            new MailTag { Id = 5, Name = "E" }
        };

        Assert.Equal(4, row.TagPieces.Count); // A、B、C + …
        Assert.Equal("…", row.TagPieces[^1].Name);
        Assert.Equal(13, row.TagPieces[^1].Corner.BottomRight); // 折叠段是末段，带右半圆
        Assert.Equal(0, row.TagPieces[2].Corner.BottomRight);   // C 后面还有折叠段，故它是直角
    }

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
