using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Core.Domain;

namespace ZungMail.App.Services;

/// <summary>移动目标选择项：账户 + 文件夹。</summary>
public sealed record MoveTargetItem(string AccountName, MailFolder Folder)
{
    public string Title => Folder.Title;
}

/// <summary>“移动到文件夹”选择器：按账户分组展示可写文件夹。</summary>
public sealed partial class FolderPickerViewModel : ObservableObject
{
    public FolderPickerViewModel(IEnumerable<MoveTargetItem> items, MailFolder? current)
    {
        Targets = new ObservableCollection<MoveTargetItem>(items);
        Selected = Targets.FirstOrDefault(item => item.Folder.Id == current?.Id) ?? Targets.FirstOrDefault();
    }

    public ObservableCollection<MoveTargetItem> Targets { get; }

    [ObservableProperty]
    private MoveTargetItem? _selected;

    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
        if (Selected is null) return;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
