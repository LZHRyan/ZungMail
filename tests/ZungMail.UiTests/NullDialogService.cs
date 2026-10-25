using ZungMail.App.Services;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;

namespace ZungMail.UiTests;

/// <summary>
/// 不打开任何窗口的对话框替身：UI 层组件（主界面、设置、阅读窗口）在测试里装配时共用，
/// 只返回「取消 / 否」，避免真实弹窗与阻塞。
/// </summary>
internal sealed class NullDialogService : IDialogService
{
    public bool ShowCompose(ComposeRequest request) => false;

    public bool ShowAccountEditor(Account? account) => false;

    public bool ShowSettings(string? sectionKey = null, long? accountId = null) => false;

    public bool ShowOutbox() => false;

    public bool ShowCleanup(CleanupScope? scope = null) => false;

    public bool ShowAddressBook() => false;

    public FollowUpCreateRequest? ShowFollowUpEditor(EmailDetail detail, string selfEmail) => null;

    public FollowUpActionRequest? ShowFollowUps() => null;

    public bool ShowShipmentCase(long caseId, Action<string>? applySearch = null) => false;

    public bool ShowFolderEditor(FolderEditRequest request) => false;

    public MailFolder? PickMoveTarget(IReadOnlyList<MoveTargetItem> targets, MailFolder? current) => null;

    public bool ShowAttachmentPreview(string fileName, string path) => false;

    public void ShowDiagnosticsReport(DiagnosticsReport report)
    {
    }

    public bool Confirm(
        string title,
        string message,
        string primaryText = "确定",
        string? detail = null,
        DialogIcon icon = DialogIcon.Question,
        bool danger = false) => true;

    public DialogChoice Ask(MessageDialogOptions options) => DialogChoice.Cancel;

    public void Alert(string title, string message, DialogIcon icon = DialogIcon.Info, string? detail = null)
    {
    }
}
