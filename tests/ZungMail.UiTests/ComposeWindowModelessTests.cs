using System;
using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 写信窗口必须是非模态的：写邮件时要能回主窗口翻邮件、复制内容、看附件。
///
/// 模态窗口 <c>ShowDialog()</c> 会把主窗口整个禁用，用户只能先关掉写信窗口；
/// 改成 <c>Show()</c> 之后又不能在窗口里设 <c>DialogResult</c>（非对话框窗口设它会抛异常），
/// 这里把两条都守住。
/// </summary>
[Trait("Category", "Ui")]
public sealed class ComposeWindowModelessTests
{
    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath));

    [Fact]
    public void 写信窗口用非模态显示()
    {
        var source = ReadSource(Path.Combine("Source", "DialogService.cs"));

        Assert.Contains("return ShowModeless(new ComposeWindow(viewModel));", source);
        Assert.Contains("window.Show();", source);
        // 其它对话框保持模态：设置、账户编辑、预览都要等用户处理完
        Assert.Contains("private static bool Show(Window window)", source);
        Assert.Contains("window.ShowDialog() == true", source);
        Assert.Contains("Show(new AccountEditWindow(viewModel))", source);
    }

    [Fact]
    public void 写信窗口不设DialogResult()
    {
        var source = ReadSource(Path.Combine("Source", "ComposeWindow.xaml.cs"));

        Assert.DoesNotContain("DialogResult = ", source);
        Assert.Contains("_sendCompleted = true;", source);
    }
}
