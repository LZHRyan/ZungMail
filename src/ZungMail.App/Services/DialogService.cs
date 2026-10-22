using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using ZungMail.Application.Services;
using ZungMail.App.ViewModels;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>撰写模式。</summary>
public enum ComposeMode
{
    New = 0,
    Reply = 1,
    ReplyAll = 2,
    Forward = 3
}

/// <summary>撰写窗口的初始内容（由主窗口在回复 / 转发时填充）。</summary>
public sealed class ComposeRequest
{
    public ComposeMode Mode { get; init; } = ComposeMode.New;
    public long? AccountId { get; init; }
    public string To { get; init; } = "";
    public string Cc { get; init; } = "";
    public string Bcc { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string? InReplyTo { get; init; }
    public string? References { get; init; }
    public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>();

    /// <summary>从草稿箱打开时带上草稿邮件的本地 id：再次保存覆盖同一封，发送成功后删掉它。</summary>
    public long? DraftEmailId { get; init; }

    public bool IsReply => Mode is ComposeMode.Reply or ComposeMode.ReplyAll;
}

/// <summary>等待回复的创建请求（由编辑窗口产出）。</summary>
public sealed record FollowUpCreateRequest(
    IReadOnlyList<string> Recipients,
    bool RequireAll,
    int MinReplies,
    bool IncludeCc,
    DateTimeOffset NextCheckUtc,
    string Note);

/// <summary>等待回复管理器里需要主窗口继续处理的动作。</summary>
public enum FollowUpActionKind
{
    OpenMessage = 0,
    Remind = 1
}

public sealed record FollowUpActionRequest(
    FollowUpActionKind Kind,
    MailFollowUp FollowUp,
    IReadOnlyList<string> Pending);

/// <summary>文件夹编辑器请求：<see cref="Existing"/> 为空时新建，否则重命名。</summary>
public sealed class FolderEditRequest
{
    public required long AccountId { get; init; }
    public required string AccountName { get; init; }
    public MailFolder? Existing { get; init; }
    public MailFolder? Parent { get; init; }
}

/// <summary>
/// 历史邮件清理的预设作用域：从账户 / 文件夹右键进来时带上，
/// 进窗口后仍然可以在界面里清除或改成别的范围。
/// </summary>
public sealed record CleanupScope(long? AccountId, long? FolderId, string Label);

/// <summary>窗口级导航服务：撰写、账户配置、设置、发件箱、文件夹编辑。</summary>
public interface IDialogService
{
    /// <summary>打开撰写窗口，返回 true 表示已发送或已加入发件箱。</summary>
    bool ShowCompose(ComposeRequest request);

    /// <summary>打开账户配置窗口，返回 true 表示已保存。</summary>
    bool ShowAccountEditor(Account? account);

    /// <summary>
    /// 打开设置窗口；<paramref name="sectionKey"/> 指定初始分区（如 templates），
    /// <paramref name="accountId"/> 用于在账户分区预选某个账户。
    /// </summary>
    bool ShowSettings(string? sectionKey = null, long? accountId = null);

    /// <summary>打开发件箱窗口。</summary>
    bool ShowOutbox();

    /// <summary>打开通讯录（地址簿）窗口。</summary>
    bool ShowAddressBook();

    /// <summary>
    /// 打开「等待回复」创建窗口；返回 null 表示取消。
    /// <paramref name="selfEmail"/> 为当前账户地址，用于把「自己」从联系人里排除。
    /// </summary>
    FollowUpCreateRequest? ShowFollowUpEditor(EmailDetail detail, string selfEmail);

    /// <summary>
    /// 打开「等待回复」管理器。返回非 null 表示用户要求打开邮件或写催信，
    /// 由主窗口负责后续的定位与撰写（保持回复引用链只有一处实现）。
    /// </summary>
    FollowUpActionRequest? ShowFollowUps();

    /// <summary>
    /// 打开「一票货」案件页（时间轴 / 时效节点 / 附件 / 资料包导出）。
    /// <paramref name="applySearch"/> 在用户点击「在主窗口筛选」时回调，参数为搜索表达式。
    /// </summary>
    bool ShowShipmentCase(long caseId, Action<string>? applySearch = null);

    /// <summary>打开历史邮件清理窗口；<paramref name="scope"/> 为预设作用域（可空，进窗口后仍可改）。</summary>
    bool ShowCleanup(CleanupScope? scope = null);

    /// <summary>打开新建 / 重命名文件夹窗口，返回 true 表示已保存。</summary>
    bool ShowFolderEditor(FolderEditRequest request);

    /// <summary>选择邮件移动目标文件夹；取消时返回 null。</summary>
    MailFolder? PickMoveTarget(IReadOnlyList<MoveTargetItem> targets, MailFolder? current);

    /// <summary>使用内置 WebView2 预览附件。</summary>
    bool ShowAttachmentPreview(string fileName, string path);

    /// <summary>展示收发诊断报告。</summary>
    void ShowDiagnosticsReport(DiagnosticsReport report);

    /// <summary>
    /// 自绘确认框（替代系统 MessageBox）：只有「主操作 / 取消」两个出口，
    /// 返回 true 表示选择了主操作；删除类操作传 <paramref name="danger"/> 让主按钮用告警色。
    /// </summary>
    bool Confirm(
        string title,
        string message,
        string primaryText = "确定",
        string? detail = null,
        DialogIcon icon = DialogIcon.Question,
        bool danger = false);

    /// <summary>自绘三态确认框（如「删除邮件 / 仅停用 / 取消」）。</summary>
    DialogChoice Ask(MessageDialogOptions options);

    /// <summary>自绘提示框（信息 / 警告 / 错误），替代系统 MessageBox。</summary>
    void Alert(string title, string message, DialogIcon icon = DialogIcon.Info, string? detail = null);
}

/// <inheritdoc />
public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _provider;

    public DialogService(IServiceProvider provider) => _provider = provider;

    public bool ShowCompose(ComposeRequest request)
    {
        var viewModel = new ComposeViewModel(
            Get<IAccountRepository>(),
            Get<IOutboxRepository>(),
            Get<IOutboxProcessor>(),
            Get<IContactRepository>(),
            Get<ITemplateStore>(),
            Get<ISettingsStore>(),
            this,
            Get<IComposeDraftStore>(),
            Get<IMailDraftService>(),
            Get<ThemeManager>(),
            Get<ILogger<ComposeViewModel>>(),
            request);

        return Show(new ComposeWindow(viewModel));
    }

    public bool ShowAccountEditor(Account? account)
    {
        var viewModel = new AccountEditViewModel(
            Get<IAccountRepository>(),
            Get<ISettingsStore>(),
            Get<IServerConfigResolver>(),
            Get<IMailSyncService>(),
            Get<ILogger<AccountEditViewModel>>(),
            account);

        return Show(new AccountEditWindow(viewModel));
    }

    public bool ShowSettings(string? sectionKey = null, long? accountId = null)
    {
        var viewModel = new SettingsViewModel(
            Get<ISettingsStore>(),
            Get<IAccountRepository>(),
            Get<ITemplateStore>(),
            Get<IDataExporter>(),
            Get<IAppPaths>(),
            Get<IMessageRepository>(),
            Get<IMailSyncService>(),
            Get<IMailDiagnosticsService>(),
            Get<IMailImportService>(),
            Get<IFolderRepository>(),
            Get<MailRuleService>(),
            Get<MailTagService>(),
            Get<SenderFilterService>(),
            Get<ContactExchangeService>(),
            Get<ClientLockService>(),
            Get<GlobalHotkeyService>(),
            Get<IStorageMonitor>(),
            Get<ICleanupService>(),
            Get<IStartupLaunchService>(),
            Get<ThemeManager>(),
            this,
            Get<IDataDirectoryService>(),
            Get<UpdateCoordinator>(),
            Get<UpdateWindowService>(),
            Get<IUpdateService>(),
            Get<IFeedbackService>(),
            Get<ILogger<SettingsViewModel>>());

        if (!string.IsNullOrWhiteSpace(sectionKey))
        {
            var section = viewModel.Sections.FirstOrDefault(s => s.Key == sectionKey);
            if (section is not null) viewModel.SelectedSection = section;
        }

        // 从账户节点右键进来时预选该账户（账户分区里默认选中的是第一个账户）
        if (accountId is { } id) viewModel.SelectAccountById(id);

        var saved = Show(new SettingsWindow(viewModel));

        // 设置窗口内点击「应用到新邮件」：关闭设置后直接打开写信窗口
        if (viewModel.PendingComposeRequest is { } request)
        {
            ShowCompose(request);
        }

        return saved;
    }

    public bool ShowOutbox()
    {
        var viewModel = new OutboxViewModel(
            Get<IOutboxRepository>(),
            Get<IOutboxProcessor>(),
            this,
            Get<ILogger<OutboxViewModel>>());

        return Show(new OutboxWindow(viewModel));
    }

    public bool ShowAddressBook()
    {
        var viewModel = new AddressBookViewModel(
            Get<IContactRepository>(),
            Get<ContactExchangeService>(),
            this,
            Get<ILogger<AddressBookViewModel>>());

        return Show(new AddressBookWindow(viewModel));
    }

    public FollowUpCreateRequest? ShowFollowUpEditor(EmailDetail detail, string selfEmail)
    {
        var viewModel = new FollowUpEditViewModel(detail, selfEmail);
        return Show(new FollowUpEditWindow(viewModel)) ? viewModel.BuildRequest() : null;
    }

    public FollowUpActionRequest? ShowFollowUps()
    {
        var viewModel = new FollowUpViewModel(
            Get<MailFollowUpService>(),
            Get<ILogger<FollowUpViewModel>>());

        FollowUpActionRequest? request = null;
        viewModel.ActionRequested += (_, value) => request = value;

        Show(new FollowUpWindow(viewModel));
        return request;
    }

    public bool ShowShipmentCase(long caseId, Action<string>? applySearch = null)
    {
        var viewModel = new ShipmentCaseViewModel(
            Get<IShipmentRepository>(),
            Get<ShipmentPackageExporter>(),
            caseId,
            applySearch,
            Get<ILogger<ShipmentCaseViewModel>>());

        return Show(new ShipmentCaseWindow(viewModel));
    }

    public bool ShowCleanup(CleanupScope? scope = null)
    {
        var viewModel = new CleanupViewModel(
            Get<ICleanupService>(),
            Get<IStorageMonitor>(),
            Get<ISettingsStore>(),
            this,
            Get<ILogger<CleanupViewModel>>());

        viewModel.ApplyScope(scope);
        return Show(new CleanupWindow(viewModel));
    }

    public bool ShowFolderEditor(FolderEditRequest request)
    {
        var viewModel = new FolderEditViewModel(
            Get<MailFolderService>(),
            request.AccountId,
            request.AccountName,
            request.Existing,
            request.Parent);

        return Show(new FolderEditWindow(viewModel));
    }

    public MailFolder? PickMoveTarget(IReadOnlyList<MoveTargetItem> targets, MailFolder? current)
    {
        if (targets.Count == 0) return null;

        var viewModel = new FolderPickerViewModel(targets, current);
        return Show(new FolderPickerWindow(viewModel)) ? viewModel.Selected?.Folder : null;
    }

    public bool ShowAttachmentPreview(string fileName, string path)
    {
        if (!File.Exists(path)) return false;

        Show(new AttachmentPreviewWindow(fileName, path, Get<IAppPaths>().RootDirectory));
        return true;
    }

    public void ShowDiagnosticsReport(DiagnosticsReport report)
        => Show(new DiagnosticsWindow(report));

    public bool Confirm(
        string title,
        string message,
        string primaryText = "确定",
        string? detail = null,
        DialogIcon icon = DialogIcon.Question,
        bool danger = false)
        => MessageDialog.Confirm(title, message, primaryText, detail, icon, danger);

    public DialogChoice Ask(MessageDialogOptions options) => MessageDialog.Ask(options);

    public void Alert(string title, string message, DialogIcon icon = DialogIcon.Info, string? detail = null)
        => MessageDialog.Alert(title, message, icon, detail);

    private T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    private static bool Show(Window window)
    {
        if (System.Windows.Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }
        return window.ShowDialog() == true;
    }
}
