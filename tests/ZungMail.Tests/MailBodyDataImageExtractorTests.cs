using System.Text;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 正文 base64 内嵌图的落盘与改写：8MB 的水单邮件正文能压到百 KB 级别，
/// 库里存的是引用而不是 6.8MB 的 base64。
/// </summary>
public sealed class MailBodyDataImageExtractorTests
{
    private static string PngBase64(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        return Convert.ToBase64String(bytes);
    }

    private static MailBodyDataImageExtractor.DataImageSink Collect(
        List<(string Mime, byte[] Bytes, int Index)> sink, string uriPrefix = "file:///C:/eml/a/")
        => (mime, bytes, index) =>
        {
            sink.Add((mime, bytes, index));
            var ext = MailBodyDataImageExtractor.ExtensionFor(mime) ?? ".bin";
            return $"{uriPrefix}inline-{index}{ext}";
        };

    [Fact]
    public void 把内嵌图改写成文件地址并回调字节()
    {
        var png = PngBase64(1024);
        var html = $"""<p>请看附图</p><img src="data:image/png;base64,{png}"><p>谢谢</p>""";
        var saved = new List<(string, byte[], int)>();

        var result = MailBodyDataImageExtractor.ReplaceDataImages(html, Collect(saved));

        Assert.DoesNotContain("base64", result);
        Assert.Contains("file:///C:/eml/a/inline-1.png", result);
        Assert.Contains("请看附图", result);
        Assert.Contains("谢谢", result);
        Assert.Single(saved);
        Assert.Equal("image/png", saved[0].Item1);
        Assert.Equal(1024, saved[0].Item2.Length);
    }

    [Fact]
    public void 多张图片按顺序编号()
    {
        var html =
            $"""<img src="data:image/png;base64,{PngBase64(64)}"><img src="data:image/jpeg;base64,{PngBase64(128)}">""";
        var saved = new List<(string, byte[], int)>();

        var result = MailBodyDataImageExtractor.ReplaceDataImages(html, Collect(saved));

        Assert.Equal(2, saved.Count);
        Assert.Contains("inline-1.png", result);
        Assert.Contains("inline-2.jpg", result);
    }

    /// <summary>有些客户端会把 base64 按 76 列折行，解码前必须能容忍空白。</summary>
    [Fact]
    public void 折行的base64也能解码()
    {
        var raw = PngBase64(512);
        var wrapped = string.Join("\r\n", Enumerable.Range(0, raw.Length / 76 + 1)
            .Select(i => i * 76 < raw.Length
                ? raw.Substring(i * 76, Math.Min(76, raw.Length - i * 76))
                : string.Empty)
            .Where(part => part.Length > 0));
        var html = $"""<img src="data:image/png;base64,{wrapped}">""";
        var saved = new List<(string, byte[], int)>();

        MailBodyDataImageExtractor.ReplaceDataImages(html, Collect(saved));

        Assert.Single(saved);
        Assert.Equal(512, saved[0].Item2.Length);
    }

    /// <summary>折行后被替换成文件地址，后面的正文不能和图片粘在一起。</summary>
    [Fact]
    public void 替换后不吞掉后面的正文()
    {
        var raw = PngBase64(256);
        var html = $"""<img src="data:image/png;base64,{raw[..76]}\r\n{raw[76..]}"> 后面的文字""";
        var saved = new List<(string, byte[], int)>();

        var result = MailBodyDataImageExtractor.ReplaceDataImages(html, Collect(saved));

        Assert.Contains("> 后面的文字", result);
    }

    [Fact]
    public void 非图片data保持原样()
    {
        var html = """<style>@font-face{src:url(data:font/woff2;base64,AAAA)}</style>"""
                   + """<img src="data:image/svg+xml;utf8,%3Csvg%3E">""";
        var saved = new List<(string, byte[], int)>();

        var result = MailBodyDataImageExtractor.ReplaceDataImages(html, Collect(saved));

        Assert.Equal(html, result);
        Assert.Empty(saved);
    }

    [Fact]
    public void 单张超过上限的图片保持原样()
    {
        var html = $"""<img src="data:image/png;base64,{PngBase64(MailBodyDataImageExtractor.MaxImageBytes + 1)}">""";
        var saved = new List<(string, byte[], int)>();

        var result = MailBodyDataImageExtractor.ReplaceDataImages(html, Collect(saved));

        Assert.Equal(html, result);
        Assert.Empty(saved);
    }

    [Fact]
    public void 落盘失败时不改正文()
    {
        var png = PngBase64(128);
        var html = $"""<img src="data:image/png;base64,{png}">""";

        var result = MailBodyDataImageExtractor.ReplaceDataImages(html, (_, _, _) => null);

        Assert.Equal(html, result);
    }

    [Fact]
    public void 没有内嵌图时原样返回()
    {
        const string html = "<p>普通正文，没有图片</p>";

        Assert.False(MailBodyDataImageExtractor.HasDataImages(html));
        Assert.Same(html, MailBodyDataImageExtractor.ReplaceDataImages(html, (_, _, _) => "x"));
    }

    /// <summary>
    /// 真实样本量级：57 张 ~120KB 的图 ≈ 6.8MB 正文，替换后应只剩引用。
    /// 用 40 张 8KB 图做等价验证，同时确认耗时在毫秒级（不能让打开邮件变慢）。
    /// </summary>
    [Fact]
    public void 批量图片替换后正文体积塌缩且耗时可控()
    {
        var png = PngBase64(8 * 1024);
        var sb = new StringBuilder("<html><body>");
        for (var i = 0; i < 40; i++)
        {
            sb.Append("<img src=\"data:image/png;base64,").Append(png).Append("\">");
        }

        sb.Append("<p>提单号 TS-NB-2608206</p></body></html>");
        var html = sb.ToString();
        var saved = new List<(string, byte[], int)>();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = MailBodyDataImageExtractor.ReplaceDataImages(html, Collect(saved));
        clock.Stop();

        Assert.Equal(40, saved.Count);
        Assert.True(result.Length < html.Length / 50, $"替换后应只剩引用：{result.Length} vs {html.Length}");
        Assert.Contains("TS-NB-2608206", result);
        Assert.True(clock.ElapsedMilliseconds < 500, $"替换耗时 {clock.ElapsedMilliseconds}ms");
    }
}
