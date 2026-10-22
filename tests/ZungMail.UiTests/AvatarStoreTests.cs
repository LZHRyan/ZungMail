using System;
using System.Threading;
using System.Windows.Media;
using ZungMail.App.Services;
using ZungMail.Core.Abstractions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 头像缓存：字节 → 已冻结画刷、统计口径、事件通知。
/// 需要 WPF 线程（<see cref="System.Windows.Media.Imaging.BitmapImage"/>），所以放在 UI 测试项目里跑 STA。
/// </summary>
public sealed class AvatarStoreTests
{
    /// <summary>1×1 透明 PNG：用来验证「是图片」与「画刷可跨线程使用」。</summary>
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public void Record_Hit_ExposesFrozenBrush()
        => RunOnStaThread(() =>
        {
            var store = new AvatarStore();
            var bytes = Convert.FromBase64String(OnePixelPng);

            store.CountQuery();
            store.Record("ops@forwarder.cn", new AvatarLookupResult(bytes, AvatarLookupStatus.Hit));

            var brush = store.GetBrush("OPS@Forwarder.CN");
            Assert.NotNull(brush);
            Assert.True(brush!.IsFrozen);
            Assert.Equal(Stretch.UniformToFill, brush.Stretch);
            Assert.True(store.HasAvatar("ops@forwarder.cn"));
            Assert.Equal(1, store.Hit);
            Assert.Equal(1, store.Count);
        });

    [Fact]
    public void Record_NotFoundAndUnavailable_AreCountedSeparately()
        => RunOnStaThread(() =>
        {
            var store = new AvatarStore();

            store.CountQuery();
            store.Record("a@b.cn", AvatarLookupResult.NotFound);
            store.CountQuery();
            store.Record("c@d.cn", AvatarLookupResult.Unavailable);

            Assert.Null(store.GetBrush("a@b.cn"));
            Assert.False(store.HasAvatar("c@d.cn"));
            Assert.Equal(1, store.NotFound);
            Assert.Equal(1, store.Unavailable);
            Assert.Equal(0, store.Hit);

            var summary = store.Describe();
            Assert.Contains("查询 2 个地址", summary);
            Assert.Contains("服务器没有 1", summary);
        });

    [Fact]
    public void Record_InvalidBytes_CountsAsDecodeFailure()
        => RunOnStaThread(() =>
        {
            var store = new AvatarStore();

            store.CountQuery();
            store.Record("a@b.cn", new AvatarLookupResult([0xFF, 0x00, 0x01], AvatarLookupStatus.Hit));

            Assert.Null(store.GetBrush("a@b.cn"));
            Assert.Equal(1, store.DecodeFailed);
            Assert.Contains("解码失败", store.Describe());
        });

    [Fact]
    public void NotifyChanged_RaisesSingleEvent()
        => RunOnStaThread(() =>
        {
            var store = new AvatarStore();
            var raised = 0;
            store.Changed += (_, _) => raised++;

            store.NotifyChanged();
            store.NotifyChanged();

            Assert.Equal(2, raised);
        });

    [Fact]
    public void GetBrush_BlankEmail_ReturnsNull()
        => RunOnStaThread(() =>
        {
            var store = new AvatarStore();

            Assert.Null(store.GetBrush(null));
            Assert.Null(store.GetBrush("   "));
            Assert.False(store.HasAvatar(""));
        });

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new InvalidOperationException(failure.Message, failure);
    }
}
