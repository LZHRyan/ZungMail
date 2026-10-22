using System.Collections.Generic;
using ZungMail.App.Services;

namespace ZungMail.UiTests;

/// <summary>查看邮件源码窗口的替身：只记录被请求打开的邮件，不创建真实窗口。</summary>
internal sealed class FakeMailSourceWindowService : IMailSourceWindowService
{
    public List<(long MessageId, string Subject)> Requests { get; } = new();

    public void Show(long messageId, string subject) => Requests.Add((messageId, subject));
}
