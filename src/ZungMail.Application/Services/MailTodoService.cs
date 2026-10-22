using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;

namespace ZungMail.Application.Services;

/// <summary>待办业务：邮件 → 待办事项，支持完成 / 重新打开 / 忽略与到期统计。</summary>
public sealed class MailTodoService
{
    private readonly IMailTodoRepository _todos;

    public MailTodoService(IMailTodoRepository todos) => _todos = todos;

    public Task<MailTodo?> GetByEmailAsync(long emailId, CancellationToken ct = default)
        => _todos.GetByEmailAsync(emailId, ct);

    public Task<IReadOnlyList<MailTodo>> QueryAsync(
        MailTodoState? state = null, bool overdueOnly = false, CancellationToken ct = default)
        => _todos.QueryAsync(state, overdueOnly, ct);

    public Task<int> CountOpenAsync(CancellationToken ct = default) => _todos.CountOpenAsync(ct);

    public Task<int> CountOverdueAsync(CancellationToken ct = default) => _todos.CountOverdueAsync(ct);

    /// <summary>新建或更新某封邮件的待办。</summary>
    public async Task<MailTodo> SaveAsync(
        long emailId, string title, DateTimeOffset? dueUtc, CancellationToken ct = default)
    {
        var todo = await _todos.GetByEmailAsync(emailId, ct).ConfigureAwait(false) ?? new MailTodo { EmailId = emailId };
        todo.Title = string.IsNullOrWhiteSpace(title) ? "跟进邮件" : title.Trim();
        todo.DueUtc = dueUtc;
        todo.State = MailTodoState.Open;
        // 截止时间可能被改动，重置提醒标记以便重新提醒。
        todo.RemindedUtc = null;
        await _todos.UpsertAsync(todo, ct).ConfigureAwait(false);
        await _todos.SetRemindedAsync(todo.Id, null, ct).ConfigureAwait(false);
        return todo;
    }

    /// <summary>取已到期且未提醒的待办。</summary>
    public Task<IReadOnlyList<MailTodo>> GetDueForReminderAsync(DateTimeOffset now, int take = 20, CancellationToken ct = default)
        => _todos.GetDueForReminderAsync(now, take, ct);

    public Task MarkRemindedAsync(long id, DateTimeOffset remindedUtc, CancellationToken ct = default)
        => _todos.SetRemindedAsync(id, remindedUtc, ct);

    public Task CompleteAsync(long id, CancellationToken ct = default)
        => _todos.SetStateAsync(id, MailTodoState.Done, ct);

    public Task ReopenAsync(long id, CancellationToken ct = default)
        => _todos.SetStateAsync(id, MailTodoState.Open, ct);

    public Task IgnoreAsync(long id, CancellationToken ct = default)
        => _todos.SetStateAsync(id, MailTodoState.Ignored, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) => _todos.DeleteAsync(id, ct);
}
