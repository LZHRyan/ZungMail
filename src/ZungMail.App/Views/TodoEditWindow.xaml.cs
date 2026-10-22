using System.Windows;
using ZungMail.Application.Services;
using ZungMail.Core.Domain;

namespace ZungMail.App.Views;

/// <summary>把一封邮件标记为待办：填写跟进内容与截止日期。</summary>
public partial class TodoEditWindow : DialogWindow
{
    private readonly MailTodoService _service;
    private readonly long _emailId;

    public TodoEditWindow(MailTodoService service, long emailId, string defaultTitle, MailTodo? existing)
    {
        InitializeComponent();
        _service = service;
        _emailId = emailId;
        TitleBox.Text = existing?.Title ?? defaultTitle;
        if (existing?.DueUtc is { } due) DueDate.SelectedDate = due.LocalDateTime.Date;
        Loaded += (_, _) => TitleBox.Focus();
    }

    public MailTodo? Result { get; private set; }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ErrorText.Text = "请填写待办内容。";
            return;
        }

        var due = DueDate.SelectedDate is { } date
            ? new DateTimeOffset(DateTime.SpecifyKind(date.Date.AddHours(18), DateTimeKind.Local))
            : (DateTimeOffset?)null;

        try
        {
            Result = await _service.SaveAsync(_emailId, title, due);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            // 保存失败不能让 async void 事件冒泡，否则会终止整个客户端。
            ErrorText.Text = "保存待办失败：" + ex.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
