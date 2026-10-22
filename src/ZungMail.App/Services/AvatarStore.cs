using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZungMail.Core.Abstractions;

namespace ZungMail.App.Services;

/// <summary>
/// 头像缓存（进程内）：把 Gravatar 拿到的图片字节解码成已冻结的 <see cref="ImageBrush"/>，
/// 供邮件头、账户节点等界面直接绑定。没有头像时返回 null，界面继续用首字母色块。
/// <para>
/// 只在后台线程写、UI 线程读：所有 Brush / BitmapImage 都 Freeze 过，可以跨线程安全共享。
/// </para>
/// </summary>
public sealed class AvatarStore
{
    private readonly ConcurrentDictionary<string, ImageBrush> _brushes = new(StringComparer.OrdinalIgnoreCase);

    private int _queried;
    private int _hit;
    private int _notFound;
    private int _unavailable;
    private int _decoded;

    /// <summary>头像集合发生变化（单张或整批加载完成），界面据此刷新绑定。</summary>
    public event EventHandler? Changed;

    /// <summary>本轮实际发出的查询数。</summary>
    public int Queried => Volatile.Read(ref _queried);

    /// <summary>本轮拿到（含本地缓存）头像的地址数。</summary>
    public int Hit => Volatile.Read(ref _hit);

    /// <summary>服务器明确没有头像的地址数。</summary>
    public int NotFound => Volatile.Read(ref _notFound);

    /// <summary>网络不可用 / 超时 / 异常的地址数。</summary>
    public int Unavailable => Volatile.Read(ref _unavailable);

    /// <summary>解码失败（拿到字节但不是图片）的地址数。</summary>
    public int DecodeFailed => Volatile.Read(ref _decoded);

    /// <summary>当前已加载到的头像数量。</summary>
    public int Count => _brushes.Count;

    /// <summary>取地址对应的头像画刷；没有则返回 null（调用方回落到首字母色块）。</summary>
    public ImageBrush? GetBrush(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        return _brushes.TryGetValue(email.Trim(), out var brush) ? brush : null;
    }

    /// <summary>该地址是否已经有头像。</summary>
    public bool HasAvatar(string? email) => GetBrush(email) is not null;

    /// <summary>记录一次查询结果并累计统计（线程安全）；有图时顺带解码入缓存。</summary>
    public void Record(string email, AvatarLookupResult result)
    {
        if (string.IsNullOrWhiteSpace(email)) return;

        if (!result.HasImage)
        {
            switch (result.Status)
            {
                case AvatarLookupStatus.Cached:
                case AvatarLookupStatus.Hit:
                    // 有图却没字节属于异常情况，按不可用计
                    Interlocked.Increment(ref _unavailable);
                    break;
                case AvatarLookupStatus.NotFound:
                    Interlocked.Increment(ref _notFound);
                    break;
                default:
                    Interlocked.Increment(ref _unavailable);
                    break;
            }

            return;
        }

        var brush = CreateBrush(result.Image!);
        if (brush is null)
        {
            Interlocked.Increment(ref _decoded);
            return;
        }

        // 统计口径：Queried 由加载器在本轮开始时累加，这里只记「有头像」
        _brushes[email.Trim()] = brush;
        Interlocked.Increment(ref _hit);
    }

    /// <summary>本轮开始时的查询计数（在加载器里调用，便于日志统计）。</summary>
    public void CountQuery() => Interlocked.Increment(ref _queried);

    /// <summary>通知界面刷新（加载器在一轮结束后集中发一次，避免逐张刷新列表）。</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>本轮统计文案（写日志用）。</summary>
    public string Describe() =>
        $"查询 {Queried} 个地址：有头像 {Hit}、服务器没有 {NotFound}、暂时取不到 {Unavailable}" +
        (DecodeFailed > 0 ? $"、解码失败 {DecodeFailed}" : "");

    /// <summary>PNG / JPEG 字节 → 已冻结的圆角填充刷；不是图片时返回 null。</summary>
    internal static ImageBrush? CreateBrush(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            brush.Freeze();
            return brush;
        }
        catch (Exception)
        {
            // 不是有效图片（服务端返回了 HTML 错误页之类）：当作没有头像
            return null;
        }
    }
}
