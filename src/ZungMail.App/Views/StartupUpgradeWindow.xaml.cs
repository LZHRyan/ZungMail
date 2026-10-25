using System;
using System.Windows;
using ZungMail.Infrastructure.Storage;

namespace ZungMail.App.Views;

/// <summary>
/// 老库升级时的进度窗口。
///
/// 升级在后台线程跑（迁移本身是同步的 SQLite 操作），这里自己实现 <see cref="IProgress{T}"/>
/// 并显式切回窗口线程——不用 <c>Progress&lt;T&gt;</c>：它依赖构造时的 SynchronizationContext，
/// 启动早期那个上下文可能还没装上，回调会落到线程池上，跨线程改控件直接抛异常。
/// </summary>
public partial class StartupUpgradeWindow : Window, IProgress<DatabaseUpgradeProgress>
{
    public StartupUpgradeWindow(int fromVersion, int toVersion)
    {
        InitializeComponent();

        VersionText.Text = $"数据版本 v{fromVersion} → v{toVersion}，升级只做一次";
        DetailText.Text = "正在准备…";
    }

    /// <summary>交给 <c>DatabaseInitializer.Initialize</c> 的进度回调。</summary>
    public IProgress<DatabaseUpgradeProgress> Progress => this;

    void IProgress<DatabaseUpgradeProgress>.Report(DatabaseUpgradeProgress value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Apply(value)));
            return;
        }

        Apply(value);
    }

    private void Apply(DatabaseUpgradeProgress progress)
    {
        DetailText.Text = progress.Total > 1
            ? $"第 {progress.Step}/{progress.Total} 步 · {progress.Detail}"
            : progress.Detail;
    }
}
