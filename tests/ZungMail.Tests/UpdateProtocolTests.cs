using System;
using System.Collections.Generic;
using System.Linq;
using ZungMail.Update.Protocol;
using Xunit;

namespace ZungMail.Tests;

/// <summary>协议契约：版本比较、地址归一化、接口路径与 JSON 形状。</summary>
public sealed class UpdateProtocolTests
{
    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("1.2.3-beta.1+build.7", true)]
    [InlineData("1.1", false)]
    [InlineData("abc", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("", false)]
    [InlineData("1.2.-3", false)]
    public void TryParse_ValidatesReleaseVersions(string text, bool expected)
        => Assert.Equal(expected, UpdateVersion.TryParse(text, out _));

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("1.2.3.4", "1.2.3", 1)]
    [InlineData("1.2.3-beta", "1.2.3", -1)]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.10", -1)]
    [InlineData("1.2.3", "1.2.3.0", 0)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    public void Compare_UsesNumericSegmentsAndPrerelease(string left, string right, int expected)
    {
        var result = UpdateVersion.Parse(left).CompareTo(UpdateVersion.Parse(right));
        Assert.Equal(expected, Math.Sign(result));
    }

    [Fact]
    public void ToString_NormalizesLeadingVAndMetadata()
    {
        Assert.Equal("1.2.3", UpdateVersion.Parse("v1.2.3+abc").ToString());
        Assert.Equal("1.2.3-beta.1", UpdateVersion.Parse("1.2.3-beta.1").ToString());
        Assert.Equal("1.2.3.4", UpdateVersion.Parse("1.2.3.4").ToString());
    }

    [Theory]
    [InlineData("updates.example.com", "http://updates.example.com/")]
    [InlineData("updates.example.com:8080", "http://updates.example.com:8080/")]
    [InlineData("http://192.168.1.20:9000/", "http://192.168.1.20:9000/")]
    [InlineData("https://updates.example.com/zungmail/", "https://updates.example.com/zungmail")]
    [InlineData("HTTPS://Updates.Example.com/base", "https://updates.example.com/base")]
    public void NormalizeAddress_AcceptsDomainIpPortAndPrefix(string input, string expected)
    {
        Assert.True(UpdateAddress.TryNormalize(input, out var address, out var error));
        Assert.Equal("", error);
        Assert.Equal(expected, address!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://updates.example.com")]
    [InlineData("http://updates.example.com/api?x=1")]
    [InlineData("https://updates.example.com/#frag")]
    [InlineData("http://")]
    public void NormalizeAddress_RejectsUnsupportedInput(string input)
    {
        Assert.False(UpdateAddress.TryNormalize(input, out var address, out var error));
        Assert.Null(address);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void BuildCheckUrl_EncodesQueryValues()
    {
        Assert.True(UpdateAddress.TryNormalize("updates.example.com/base", out var address, out _));
        var url = UpdateRoutes.BuildCheckUrl(address!, "1.0.0", "beta");

        Assert.Equal(
            "http://updates.example.com/base/api/update/check?current=1.0.0&channel=beta&os=windows&arch=x64",
            url);
    }

    [Theory]
    [InlineData("/api/update/download/stable-1.1.0", "http://updates.example.com/base/api/update/download/stable-1.1.0")]
    [InlineData("https://cdn.example.com/a.exe", "https://cdn.example.com/a.exe")]
    public void ResolveDownloadUri_SupportsRelativeAndAbsolute(string downloadUrl, string expected)
    {
        Assert.True(UpdateAddress.TryNormalize("http://updates.example.com/base", out var address, out _));
        Assert.Equal(expected, UpdateRoutes.ResolveDownloadUri(address!, downloadUrl)!.ToString());
    }

    [Fact]
    public void CheckResponse_DeserializesServerPayload()
    {
        const string json = """
        {
          "protocol": 1,
          "serverTime": "2026-10-19T08:00:00Z",
          "update": {
            "id": "stable-1.1.0",
            "version": "1.1.0",
            "channel": "stable",
            "notes": "修复若干问题",
            "mandatory": false,
            "fileName": "ZungMail-1.1.0-win-x64-setup.exe",
            "size": 1048576,
            "sha256": "abc",
            "publishedAt": "2026-10-19T07:59:00Z",
            "downloadUrl": "/api/update/download/stable-1.1.0"
          }
        }
        """;

        var payload = System.Text.Json.JsonSerializer.Deserialize<UpdateCheckResponse>(
            json, UpdateJson.Options)!;

        Assert.Equal(1, payload.Protocol);
        Assert.NotNull(payload.Update);
        Assert.Equal("1.1.0", payload.Update!.Version);
        Assert.Equal(1048576, payload.Update.Size);
        Assert.Null(payload.Update.MinVersion);
    }

    [Fact]
    public void DuplicateVersionScenarios_AreComparable()
    {
        var versions = new[] { "1.0.0", "1.10.0", "1.9.9", "1.2.3-beta.2", "1.2.3-beta.10" };
        var parsed = versions.Select(UpdateVersion.Parse).ToList();

        Assert.True(parsed[1] > parsed[2]);
        Assert.True(parsed[4] > parsed[3]);
    }

    [Theory]
    [InlineData("ZungMail-1.1.0-x64.msi", ".msi")]
    [InlineData(@"C:\updates\1.1.0\ZungMail-1.1.0-setup.exe", ".exe")]
    [InlineData("ZungMail-1.1.0-portable.ZIP", ".zip")]
    [InlineData("https://cdn.example.com/a/Setup.EXE?token=1#frag", ".exe")]
    [InlineData("ZungMail-setup-noext", "")]
    [InlineData("", "")]
    public void GetExtension_HandlesNamePathAndUrl(string input, string expected)
        => Assert.Equal(expected, UpdatePackage.GetExtension(input));

    [Theory]
    [InlineData("ZungMail-1.1.0-x64.msi", UpdatePackageKind.Installer)]
    [InlineData("ZungMail-1.1.0-win-x64-selfcontained-setup.exe", UpdatePackageKind.Installer)]
    [InlineData("ZungMail-1.1.0-win-x64-selfcontained-portable.zip", UpdatePackageKind.Portable)]
    [InlineData("/api/update/download/stable-1.1.0", UpdatePackageKind.Unknown)]
    public void Classify_RecognisesInstallerAndPortable(string input, UpdatePackageKind expected)
        => Assert.Equal(expected, UpdatePackage.Classify(input));

    [Fact]
    public void PackageHelpers_AgreeWithKind()
    {
        Assert.True(UpdatePackage.IsPortable("ZungMail-1.1.0-portable.zip"));
        Assert.False(UpdatePackage.IsInstaller("ZungMail-1.1.0-portable.zip"));
        Assert.True(UpdatePackage.IsInstaller("ZungMail-1.1.0-x64.msi"));

        // 便携版必须在界面上被明确标注，避免用户以为下完就能自动安装
        Assert.Equal("便携版 zip", UpdatePackage.Describe("ZungMail-1.1.0-portable.zip"));
        Assert.Equal("安装程序", UpdatePackage.Describe("ZungMail-1.1.0-x64.msi"));
        Assert.Equal("未知类型", UpdatePackage.Describe("ZungMail-1.1.0"));
    }
}
