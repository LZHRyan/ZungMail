using System.Collections.ObjectModel;
using System.Windows;
using ZungMail.Application.Services;
using ZungMail.Core.Domain;

namespace ZungMail.App.Views;

/// <summary>标签选择：勾选即打标签，取消勾选即移除；支持现场新建标签（归属当前邮件所在账户）。</summary>
public partial class TagPickerWindow : DialogWindow
{
    private readonly MailTagService _service;
    private readonly IReadOnlyList<long> _emailIds;
    private readonly long _accountId;
    private readonly ObservableCollection<TagRow> _rows = new();

    public TagPickerWindow(
        MailTagService service,
        IReadOnlyList<long> emailIds,
        IReadOnlyList<MailTag> tags,
        IReadOnlySet<long> applied,
        long accountId)
    {
        InitializeComponent();
        _service = service;
        _emailIds = emailIds;
        _accountId = accountId;

        foreach (var tag in tags) _rows.Add(new TagRow(tag) { IsChecked = applied.Contains(tag.Id) });
        TagList.ItemsSource = _rows;
    }

    public sealed class TagRow : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isChecked;

        public TagRow(MailTag tag)
        {
            Id = tag.Id;
            Name = tag.Name;
        }

        public long Id { get; }
        public string Name { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private async void OnCreateTagClick(object sender, RoutedEventArgs e)
    {
        var name = NewTagBox.Text.Trim();
        if (name.Length == 0) return;

        var tag = new MailTag { Name = name, SortOrder = _rows.Count, AccountId = _accountId };
        var (ok, error) = await _service.SaveAsync(tag);
        if (!ok)
        {
            ErrorText.Text = error ?? "新建失败";
            return;
        }

        ErrorText.Text = "";
        NewTagBox.Text = "";
        var row = new TagRow(tag) { IsChecked = true };
        _rows.Add(row);
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var row in _rows)
            {
                await _service.ApplyAsync(_emailIds, row.Id, row.IsChecked);
            }
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            // 事件处理器不能抛出：失败时留在窗口内提示，避免未处理异常终止客户端。
            ErrorText.Text = "应用标签失败：" + ex.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
