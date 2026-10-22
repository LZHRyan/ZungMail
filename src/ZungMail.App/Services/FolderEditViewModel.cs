using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Application.Services;
using ZungMail.Core.Domain;

namespace ZungMail.App.Services;

/// <summary>新建 / 重命名文件夹对话框的最小输入模型。</summary>
public sealed partial class FolderEditViewModel : ObservableObject
{
    private readonly MailFolderService _service;
    private readonly long _accountId;
    private readonly MailFolder? _existing;
    private readonly MailFolder? _parent;

    public FolderEditViewModel(
        MailFolderService service,
        long accountId,
        string accountName,
        MailFolder? existing = null,
        MailFolder? parent = null)
    {
        _service = service;
        _accountId = accountId;
        _existing = existing;
        _parent = parent;

        AccountName = accountName;
        ParentPath = parent?.Title ?? "根目录";
        Name = existing?.DisplayName ?? existing?.FullName ?? string.Empty;
    }

    public string Title => _existing is null ? "新建文件夹" : "重命名文件夹";

    public string AccountName { get; }

    public string ParentPath { get; }

    public string FullPathPreview => _parent is null ? Name : $"{_parent.FullName} / {Name}";

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isBusy;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(FullPathPreview));

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusText = "正在保存…";
        var result = _existing is null
            ? await _service.CreateAsync(_accountId, _parent, Name)
            : await _service.RenameAsync(_accountId, _existing, Name);

        IsBusy = false;
        if (result.Success)
        {
            CloseRequested?.Invoke(this, true);
            return;
        }

        StatusText = result.Error ?? "操作失败";
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
