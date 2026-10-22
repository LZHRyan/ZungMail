using System;
using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;
using ZungMail.App.ViewModels;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>x64 构建约束：测试进程、客户端程序集与测试程序集都必须是 AMD64。</summary>
[Trait("Category", "Ui")]
public class X64PackagingTests
{
    [Fact]
    public void 测试进程按x64运行()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(System.Runtime.InteropServices.Architecture.X64, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
    }

    [Fact]
    public void 客户端与测试程序集均为x64()
    {
        AssertMachine(Assembly.GetExecutingAssembly().Location, "测试程序集");
        AssertMachine(typeof(MainViewModel).Assembly.Location, "ZungMail 客户端");
    }

    private static void AssertMachine(string path, string label)
    {
        Assert.True(File.Exists(path), $"{label}不存在：{path}");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
    }
}
