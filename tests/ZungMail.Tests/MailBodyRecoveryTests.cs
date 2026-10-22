using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 「重新获取正文」入口的判定：正文为空时才给，草稿与下载中不给。
/// 主窗口与独立阅读窗口共用这份规则（<see cref="MailBodyRecovery"/>）。
/// </summary>
public sealed class MailBodyRecoveryTests
{
    private static EmailDetail Detail(
        string html = "", string text = "", MessageFlags flags = MessageFlags.None)
        => new()
        {
            BodyHtml = html,
            BodyText = text,
            BodyState = BodyState.Downloaded,
            Flags = flags
        };

    [Fact]
    public void 正文为空且不是草稿_提供重新获取正文()
        => Assert.True(MailBodyRecovery.ShouldOfferReload(Detail(), isBodyLoading: false));

    [Fact]
    public void 只有纯文本正文_不提供()
        => Assert.False(MailBodyRecovery.ShouldOfferReload(Detail(text: "Dear Anna,"), isBodyLoading: false));

    [Fact]
    public void 只有HTML正文_不提供()
        => Assert.False(MailBodyRecovery.ShouldOfferReload(Detail(html: "<p>Dear</p>"), isBodyLoading: false));

    [Fact]
    public void 草稿_不提供()
        => Assert.False(MailBodyRecovery.ShouldOfferReload(Detail(flags: MessageFlags.Draft), isBodyLoading: false));

    [Fact]
    public void 正文正在下载_不提供()
        => Assert.False(MailBodyRecovery.ShouldOfferReload(Detail(), isBodyLoading: true));

    [Fact]
    public void 没有选中邮件_不提供()
        => Assert.False(MailBodyRecovery.ShouldOfferReload(null, isBodyLoading: false));
}
