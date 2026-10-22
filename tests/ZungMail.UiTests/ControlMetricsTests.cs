using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 控件高度基准回归：设置页里「输入框 / 密码框 / 下拉框 / 按钮」并排展示，必须共用同一高度，
/// 否则会像之前那样输入框比按钮高一截（TextBox 模板里二次叠加 Padding 的坑）。
/// 这里直接加载应用主题，量一遍四种控件的实际高度，保证它们始终一致。
/// </summary>
[Trait("Category", "Ui")]
public sealed class ControlMetricsTests
{
    [Fact]
    public void InputComboAndButtonShareSameHeight()
    {
        var heights = RunOnStaThread(MeasureControlHeights);

        Assert.Equal(32, heights.TextBox);
        Assert.Equal(32, heights.PasswordBox);
        Assert.Equal(32, heights.ComboBox);
        Assert.Equal(32, heights.Button);
        Assert.Equal(heights.TextBox, heights.Button);
        Assert.Equal(heights.TextBox, heights.ComboBox);
    }

    private static (double TextBox, double PasswordBox, double ComboBox, double Button) MeasureControlHeights()
    {
        // 直接解析主题源文件：源文件里的 StaticResource 都定义在同一份字典内，
        // 不需要 pack:// 方案，也不用在测试进程里创建 WPF Application（会把其它测试一起卡死）。
        var themePath = Path.Combine(AppContext.BaseDirectory, "Theme", "Controls.xaml");
        Assert.True(File.Exists(themePath), "缺少主题文件：" + themePath);

        // x:Shared 只允许出现在编译后的资源字典里，测试里用 XamlReader 解析源文件时去掉它：
        // 这条属性只影响「同一个 ContextMenu 实例是否复用」，和控件高度无关。
        var xaml = File.ReadAllText(themePath).Replace(" x:Shared=\"False\"", string.Empty, StringComparison.Ordinal);
        var dictionary = (ResourceDictionary)XamlReader.Parse(xaml);

        var textBox = new TextBox { Style = (Style)dictionary["AppTextBox"], Text = "1" };
        var passwordBox = new PasswordBox { Style = (Style)dictionary["AppPasswordBox"] };
        var comboBox = new ComboBox { Style = (Style)dictionary["AppComboBox"] };
        comboBox.Items.Add("选项 1");
        comboBox.SelectedIndex = 0;
        var button = new Button { Style = (Style)dictionary["Btn"], Content = "按钮" };

        var panel = new StackPanel();
        panel.Children.Add(textBox);
        panel.Children.Add(passwordBox);
        panel.Children.Add(comboBox);
        panel.Children.Add(button);

        // 只做测量，不创建窗口：DesiredSize 与真实界面上分到的高度一致
        panel.Measure(new Size(320, double.PositiveInfinity));

        return (textBox.DesiredSize.Height, passwordBox.DesiredSize.Height, comboBox.DesiredSize.Height, button.DesiredSize.Height);
    }

    private static T RunOnStaThread<T>(Func<T> action) where T : struct
    {
        var result = default(T);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // 只量尺寸就不需要 Application：主题里的 DynamicResource 在测量阶段不会被解析，
                // 而创建 Application / Dispatcher 会污染同进程里其它视图模型测试，导致整个测试进程卡住。
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new InvalidOperationException("控件高度测量超时（STA 线程未结束）");
        }

        if (failure != null)
        {
            throw new InvalidOperationException("控件高度测量失败：" + failure.Message, failure);
        }

        return result;
    }
}
