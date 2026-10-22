using System.Windows;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>通讯录窗口：搜索、编辑、写信与 vCard/CSV 导入导出。</summary>
public partial class AddressBookWindow : DialogWindow
{
    private readonly AddressBookViewModel _viewModel;

    public AddressBookWindow(AddressBookViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
