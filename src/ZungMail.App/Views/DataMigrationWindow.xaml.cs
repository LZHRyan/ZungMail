using System.Windows;
using ZungMail.Core.Abstractions;

namespace ZungMail.App.Views;

/// <summary>
/// 启动期数据目录迁移的进度窗口：在构建宿主、打开数据库之前显示，
/// 迁移完成（或失败）后由调用方关闭。
/// </summary>
public partial class DataMigrationWindow : DialogWindow
{
    public DataMigrationWindow(string source, string target)
    {
        InitializeComponent();
        SourceText.Text = source;
        TargetText.Text = target;
    }

    /// <summary>把迁移进度反映到界面（由 UI 线程上的 IProgress 调用）。</summary>
    public void Report(DataMigrationProgress progress)
    {
        StageText.Text = progress.Stage;
        DetailText.Text = progress.Detail;
        PercentText.Text = progress.Percent.ToString("0") + "%";
        Progress.Value = progress.Percent;
    }

    /// <summary>迁移期间不允许关闭，避免用户误以为能中断。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_completed)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private bool _completed;

    /// <summary>标记迁移已结束，允许关闭窗口。</summary>
    public void MarkCompleted()
    {
        _completed = true;
        Close();
    }
}
