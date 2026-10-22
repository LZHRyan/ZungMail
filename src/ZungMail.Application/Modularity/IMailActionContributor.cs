using ZungMail.Core.Domain;

namespace ZungMail.Application.Modularity;

/// <summary>为邮件列表、右键菜单或阅读区贡献业务动作的契约。</summary>
public interface IMailActionContributor
{
    IEnumerable<MailActionDefinition> GetMailActions();
}

/// <summary>
/// 与 WPF 无关的邮件动作定义。宿主负责把可见性、启用状态和执行结果映射为具体控件与提示。
/// </summary>
public sealed record MailActionDefinition(
    string Id,
    string Title,
    Func<IReadOnlyList<EmailSummary>, bool> CanExecute,
    Func<IReadOnlyList<EmailSummary>, CancellationToken, Task> ExecuteAsync,
    string? IconKey = null,
    int Order = 0);
