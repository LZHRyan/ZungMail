using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 地址簿（Foxmail「通讯录」）：搜索、新建/编辑、删除、白名单、从联系人写信，
/// 以及复用 <see cref="ContactExchangeService"/> 的 vCard / CSV 导入导出。
/// </summary>
public sealed partial class AddressBookViewModel : ObservableObject
{
    private readonly IContactRepository _contacts;
    private readonly ContactExchangeService _exchange;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AddressBookViewModel> _logger;

    public AddressBookViewModel(
        IContactRepository contacts,
        ContactExchangeService exchange,
        IDialogService dialogs,
        ILogger<AddressBookViewModel> logger)
    {
        _contacts = contacts;
        _exchange = exchange;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<Contact> Contacts { get; } = new();

    /// <summary>地址簿每页条数：联系人可能上万，列表必须分页（与邮件列表同样的做法）。</summary>
    public const int PageSize = 50;

    [ObservableProperty] private Contact? _selected;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editEmail = "";
    [ObservableProperty] private bool _editWhitelisted;
    [ObservableProperty] private string _statusText = "共 0 位联系人";

    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalCount;

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public bool CanGoPrevious => CurrentPage > 1;

    public bool CanGoNext => CurrentPage < TotalPages;

    public string PageInfo => $"第 {CurrentPage} / {TotalPages} 页";

    /// <summary>只有一页时整条分页栏收起，省出列表高度。</summary>
    public bool ShowPager => TotalCount > PageSize;

    /// <summary>当前是否处于「编辑已有联系人」状态（决定保存是更新还是新增）。</summary>
    private long _editingId;

    public async Task InitializeAsync()
    {
        await ReloadAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        // 换了搜索词就从第一页重新看
        CurrentPage = 1;
        _ = ReloadAsync();
    }

    partial void OnCurrentPageChanged(int value) => RaisePagerChanged();

    partial void OnTotalCountChanged(int value) => RaisePagerChanged();

    private void RaisePagerChanged()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(ShowPager));
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (!CanGoPrevious) return;
        CurrentPage--;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanGoNext) return;
        CurrentPage++;
        await ReloadAsync();
    }

    /// <summary>清空搜索（搜索框右侧的 ×）。</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        if (SearchText.Length == 0) return;
        SearchText = "";
    }

    partial void OnSelectedChanged(Contact? value)
    {
        if (value is null)
        {
            _editingId = 0;
            EditName = "";
            EditEmail = "";
            EditWhitelisted = false;
            return;
        }

        _editingId = value.Id;
        EditName = value.Name;
        EditEmail = value.Email;
        EditWhitelisted = value.IsWhitelisted;
    }

    [RelayCommand]
    private void NewContact()
    {
        Selected = null;
        _editingId = 0;
        EditName = "";
        EditEmail = "";
        EditWhitelisted = false;
        StatusText = "填写姓名与邮箱后点击「保存联系人」";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var email = (EditEmail ?? "").Trim().ToLowerInvariant();
        if (email.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
        {
            StatusText = "请输入有效的邮箱地址";
            return;
        }

        try
        {
            await _contacts.UpsertManyAsync(new[]
            {
                new Contact { Email = email, Name = (EditName ?? "").Trim(), IsWhitelisted = EditWhitelisted }
            });

            // 白名单是独立列，UpsertMany 不覆盖它，因此单独同步一次。
            await _contacts.SetWhitelistAsync(email, EditWhitelisted);

            await ReloadAsync();
            Selected = Contacts.FirstOrDefault(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));
            StatusText = _editingId == 0 ? $"已添加：{email}" : $"已更新：{email}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存联系人失败");
            StatusText = "保存失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } contact) return;

        try
        {
            await _contacts.DeleteAsync(contact.Id);
            NewContact();
            await ReloadAsync();
            StatusText = $"已删除：{contact.Email}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除联系人失败");
            StatusText = "删除失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleWhitelistAsync()
    {
        if (Selected is not { } contact) return;

        var next = !contact.IsWhitelisted;
        try
        {
            await _contacts.SetWhitelistAsync(contact.Email, next);
            await ReloadAsync();
            Selected = Contacts.FirstOrDefault(c => c.Id == contact.Id);
            StatusText = next ? $"已加入白名单：{contact.Email}" : $"已移出白名单：{contact.Email}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换白名单失败");
            StatusText = "操作失败：" + ex.Message;
        }
    }

    /// <summary>用选中联系人作为收件人打开发信窗口。</summary>
    [RelayCommand]
    private void ComposeTo()
    {
        if (Selected is not { } contact) return;

        _dialogs.ShowCompose(new ComposeRequest { To = contact.Email });
        StatusText = $"已打开写信窗口，收件人：{contact.Email}";
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入联系人",
            Filter = "联系人文件 (*.vcf;*.csv)|*.vcf;*.csv|vCard (*.vcf)|*.vcf|CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog() != true) return;

        var result = await _exchange.ImportAsync(dialog.FileName);
        StatusText = result.Message;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出联系人",
            Filter = "vCard (*.vcf)|*.vcf|CSV (*.csv)|*.csv",
            FileName = "contacts.vcf",
            DefaultExt = ".vcf",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;

        var result = await _exchange.ExportAsync(dialog.FileName);
        StatusText = result.Message;
    }

    private async Task ReloadAsync()
    {
        try
        {
            var filter = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
            var page = await _contacts.QueryAsync(filter, (CurrentPage - 1) * PageSize, PageSize);

            // 删除 / 搜索后当前页可能超出范围：退回最后一页再查一次
            if (page.Items.Count == 0 && CurrentPage > 1 && page.TotalCount > 0)
            {
                CurrentPage = Math.Max(1, (int)Math.Ceiling(page.TotalCount / (double)PageSize));
                page = await _contacts.QueryAsync(filter, (CurrentPage - 1) * PageSize, PageSize);
            }

            TotalCount = page.TotalCount;
            Contacts.Clear();
            foreach (var contact in page.Items) Contacts.Add(contact);

            var prefix = filter is null ? "共" : "匹配";
            StatusText = TotalCount == 0
                ? (filter is null ? "还没有联系人" : "没有匹配的联系人")
                : $"{prefix} {TotalCount} 位联系人 · 第 {CurrentPage} / {TotalPages} 页";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载联系人失败");
            StatusText = "加载失败：" + ex.Message;
        }
    }
}
