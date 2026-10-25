using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using ZungMail.App.ViewModels;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 启动体验回归：
/// 1) 老库升级要先弹进度窗口（机械盘上实测 163 秒，不能假死）；
/// 2) 邮件列表整批替换只发一次 Reset（逐条 Add 会把首屏拖到一分半）。
/// </summary>
[Trait("Category", "Ui")]
public sealed class StartupUpgradeUiTests
{
    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath));

    [Fact]
    public void 升级进度窗口有版本说明与步骤文案()
    {
        var xaml = ReadSource(Path.Combine("Views", "StartupUpgradeWindow.xaml"));

        Assert.Contains("正在升级本地数据", xaml);
        Assert.Contains("x:Name=\"VersionText\"", xaml);
        Assert.Contains("x:Name=\"DetailText\"", xaml);
        Assert.Contains("x:Name=\"StepBar\"", xaml);
        Assert.Contains("请不要关闭程序", xaml);
        Assert.Contains("Topmost=\"True\"", xaml);
    }

    [Fact]
    public void 启动流程在老库升级时先弹进度窗口再后台初始化()
    {
        var source = ReadSource(Path.Combine("Source", "App.xaml.cs"));

        Assert.Contains("RunDatabaseInitializationAsync", source);
        Assert.Contains("PeekSchemaVersion()", source);
        Assert.Contains("new StartupUpgradeWindow(", source);
        // 迁移必须离开 UI 线程，否则进度窗口画不出来
        Assert.Contains("Task.Run(() => initializer.Initialize(splash.Progress))", source);
        // 全新安装不弹窗，避免闪一下
        Assert.Contains("current <= 0", source);
    }

    [Fact]
    public void 列表整批替换只发一次Reset()
    {
        var collection = new ObservableRangeCollection<int>();
        var notifications = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => notifications.Add(e.Action);

        collection.ReplaceAll(new[] { 1, 2, 3, 4, 5 });

        Assert.Equal(5, collection.Count);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, notifications);
    }
}
