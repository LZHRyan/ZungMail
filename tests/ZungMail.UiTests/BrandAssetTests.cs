using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 品牌资源回归：图标必须是 16–256 的七档 32bpp 帧、界面引用的是同一套新 Z 徽标、
/// csproj 同时把它注册成资源与可执行文件图标（任务栏 / Alt+Tab / 资源管理器 / 安装包都靠它）。
/// </summary>
[Trait("Category", "Ui")]
public sealed class BrandAssetTests
{
    private static string BrandPath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Brand", fileName);

    [Fact]
    public void 图标包含七档尺寸且都是32bpp帧()
    {
        var path = BrandPath("ZungMail.ico");
        Assert.True(File.Exists(path), "缺少应用图标：" + path);

        var bytes = File.ReadAllBytes(path);

        // ICONDIR: reserved(2)=0, type(2)=1, count(2)
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        Assert.Equal(7, count);

        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];   // 0 表示 256
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry + 6, 2));
            var bytesInRes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 8, 4));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 12, 4));

            Assert.Equal(width, height);
            Assert.Equal(32, bitCount);
            Assert.True(bytesInRes > 0, $"{width}px 帧为空");
            Assert.True(offset + bytesInRes <= bytes.Length, $"{width}px 帧越界");
            sizes.Add(width);
        }

        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes);
    }

    [Theory]
    [InlineData("ZungMailMark64.png", 64)]
    [InlineData("ZungMailMark.png", 256)]
    public void 界面用PNG与显示尺寸匹配(string fileName, int expected)
    {
        var path = BrandPath(fileName);
        Assert.True(File.Exists(path), "缺少品牌 PNG：" + path);

        // PNG IHDR：8 字节签名后是长度+类型，随后 width(4) / height(4) 大端
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));

        Assert.Equal(expected, width);
        Assert.Equal(expected, height);
    }

    [Fact]
    public void 工程把图标注册为资源与可执行文件图标()
    {
        var path = BrandPath("ZungMail.App.csproj");
        Assert.True(File.Exists(path), "缺少 App 工程文件：" + path);

        var csproj = File.ReadAllText(path);
        Assert.Contains(@"<ApplicationIcon>Assets\Brand\ZungMail.ico</ApplicationIcon>", csproj);
        Assert.Contains(@"<Resource Include=""Assets\Brand\ZungMail.ico""", csproj);
        Assert.Contains(@"<Resource Include=""Assets\Brand\ZungMailMark64.png""", csproj);
        Assert.Contains(@"<Resource Include=""Assets\Brand\ZungMailMark.png""", csproj);
    }

    [Fact]
    public void 标题栏与关于页显示新徽标()
    {
        var main = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));
        Assert.Contains("/Assets/Brand/ZungMailMark64.png", main);

        var settings = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml"));
        Assert.Contains("/Assets/Brand/ZungMailMark.png", settings);
        // 关于页的「个人版」徽标仍在，版本号依旧只在标题栏
        Assert.Contains("个人版", settings);
    }

    [Theory]
    [InlineData("ZungMail.ico", "zungmail.ico")]
    [InlineData("ZungMailMark.png", "zungmail-256.png")]
    [InlineData("ZungMailMark64.png", "zungmail-64.png")]
    public void 应用内图标与docs母版逐字节一致(string appFile, string masterFile)
    {
        var app = Path.Combine(AppContext.BaseDirectory, "Brand", appFile);
        var master = Path.Combine(AppContext.BaseDirectory, "Brand", "Master", masterFile);

        Assert.True(File.Exists(app), "缺少应用内图标：" + app);
        Assert.True(File.Exists(master), "缺少品牌母版：" + master);

        // 换图标时最容易漏的就是「改了 docs 母版但没覆盖应用内副本」，这里直接比字节
        Assert.True(
            File.ReadAllBytes(app).AsSpan().SequenceEqual(File.ReadAllBytes(master)),
            $"{appFile} 与母版 {masterFile} 不一致：先按 docs/brand/README.md 的流程同步应用内副本");
    }
}
