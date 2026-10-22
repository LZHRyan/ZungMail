using ZungMail.Application.Services;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 标签快捷键：单键规则、按账户隔离（名称 / 键位同账户唯一）、
/// 以及「选中邮件按一下打 / 取消标签」的批量切换语义。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MailTagShortcutTests : IDisposable
{
    private const long AccountA = 1;
    private const long AccountB = 2;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "zungmail-tags-" + Guid.NewGuid().ToString("N")[..8]);

    public MailTagShortcutTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(factory).Initialize();
        Service = new MailTagService(new SqliteMailTagRepository(factory));
    }

    private MailTagService Service { get; }

    [Fact]
    public async Task 单键规范化与账户内唯一校验()
    {
        var booking = new MailTag { Name = "订舱", AccountId = AccountA, ShortcutKey = " a " };
        Assert.True((await Service.SaveAsync(booking)).Ok);
        Assert.Equal("A", booking.ShortcutKey);
        Assert.Equal("A", booking.ShortcutDisplay);

        // 同账户键位冲突
        var customs = new MailTag { Name = "报关", AccountId = AccountA, ShortcutKey = "A" };
        var duplicateKey = await Service.SaveAsync(customs);
        Assert.False(duplicateKey.Ok);
        Assert.Contains("已被该账户的其它标签占用", duplicateKey.Error);

        // 同账户重名
        var sameName = new MailTag { Name = "订舱", AccountId = AccountA, ShortcutKey = "2" };
        var duplicateName = await Service.SaveAsync(sameName);
        Assert.False(duplicateName.Ok);
        Assert.Contains("同名标签", duplicateName.Error);

        // 不同账户可以用同一个键
        var other = new MailTag { Name = "订舱", AccountId = AccountB, ShortcutKey = "A" };
        Assert.True((await Service.SaveAsync(other)).Ok);

        // 解绑后同账户的键位可以被别人复用
        booking.ShortcutKey = null;
        Assert.True((await Service.SaveAsync(booking)).Ok);
        Assert.Empty(booking.ShortcutDisplay);
        customs.ShortcutKey = "A";
        Assert.True((await Service.SaveAsync(customs)).Ok);
    }

    [Fact]
    public async Task 非法键位与未选账户被拒绝()
    {
        Assert.False((await Service.SaveAsync(new MailTag { Name = "组合键", AccountId = AccountA, ShortcutKey = "Ctrl+K" })).Ok);
        Assert.False((await Service.SaveAsync(new MailTag { Name = "F5", AccountId = AccountA, ShortcutKey = "F5" })).Ok);
        Assert.False((await Service.SaveAsync(new MailTag { Name = "无账户", ShortcutKey = "3" })).Ok);

        Assert.True(TagShortcutKeys.IsAllowed("1"));
        Assert.True(TagShortcutKeys.IsAllowed("Z"));
        Assert.True(TagShortcutKeys.IsAllowed("F12"));
        Assert.False(TagShortcutKeys.IsAllowed("F5"));
        Assert.False(TagShortcutKeys.IsAllowed("F1"));
        Assert.False(TagShortcutKeys.IsAllowed("Ctrl+1"));
    }

    [Fact]
    public async Task 按单键打标签_再按一次取消()
    {
        var tag = new MailTag { Name = "异常", AccountId = AccountA, ShortcutKey = "1" };
        Assert.True((await Service.SaveAsync(tag)).Ok);

        var messages = new List<(long EmailId, long AccountId)> { (101, AccountA), (102, AccountA) };

        var applied = await Service.ToggleByShortcutAsync(messages, "1");
        Assert.Equal(2, applied.Applied);
        Assert.Equal(0, applied.Removed);
        Assert.Single(await Service.GetForMessageAsync(101));

        var removed = await Service.ToggleByShortcutAsync(messages, "1");
        Assert.Equal(0, removed.Applied);
        Assert.Equal(2, removed.Removed);
        Assert.Empty(await Service.GetForMessageAsync(101));
    }

    [Fact]
    public async Task 部分已打时统一补齐而不是取消()
    {
        var tag = new MailTag { Name = "待跟进", AccountId = AccountA, ShortcutKey = "2" };
        Assert.True((await Service.SaveAsync(tag)).Ok);
        await Service.AddToMessageAsync(201, tag.Id);

        var result = await Service.ToggleByShortcutAsync(
            new List<(long, long)> { (201, AccountA), (202, AccountA) }, "2");

        Assert.Equal(2, result.Applied); // 只有一封已打 → 视为「补齐」
        Assert.Single(await Service.GetForMessageAsync(202));
    }

    [Fact]
    public async Task 跨账户多选时各按自己账户的标签处理()
    {
        var a = new MailTag { Name = "异常", AccountId = AccountA, ShortcutKey = "3" };
        var b = new MailTag { Name = "异常", AccountId = AccountB, ShortcutKey = "3" };
        Assert.True((await Service.SaveAsync(a)).Ok);
        Assert.True((await Service.SaveAsync(b)).Ok);

        var result = await Service.ToggleByShortcutAsync(
            new List<(long, long)> { (301, AccountA), (302, AccountB) }, "3");

        Assert.Equal(2, result.Applied);
        Assert.Equal(a.Id, Assert.Single(await Service.GetForMessageAsync(301)).Id);
        Assert.Equal(b.Id, Assert.Single(await Service.GetForMessageAsync(302)).Id);
    }

    [Fact]
    public async Task 账户没有绑定该键时跳过并提示()
    {
        var a = new MailTag { Name = "异常", AccountId = AccountA, ShortcutKey = "4" };
        Assert.True((await Service.SaveAsync(a)).Ok);

        var result = await Service.ToggleByShortcutAsync(
            new List<(long, long)> { (401, AccountA), (402, AccountB) }, "4");

        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Contains("没有绑定", result.Describe());

        var nothing = await Service.ToggleByShortcutAsync(new List<(long, long)> { (403, AccountB) }, "4");
        Assert.False(nothing.Changed);
        Assert.Contains("没有绑定", nothing.Describe());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响验证结论
        }
    }
}
