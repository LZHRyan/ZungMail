using ZungMail.Application.Services;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>规则条件与动作：发件域条件、打标签动作参数、持久化往返。</summary>
[Trait("Category", "Unit")]
public sealed class MailRuleMatchTests
{
    [Fact]
    public void 发件域命中_整域与子域都算()
    {
        var rule = new MailRule { FromDomain = "maersk.com", Action = MailRuleAction.AddTag };

        Assert.True(MailRuleMatch.Matches(rule, "Maersk", "booking@maersk.com", "SO Release"));
        Assert.True(MailRuleMatch.Matches(rule, "Maersk", "ops@shanghai.maersk.com", "SO Release"));
        Assert.False(MailRuleMatch.Matches(rule, "Other", "ops@example.com", "SO Release"));
    }

    [Fact]
    public void 发件域带前导at也能用()
    {
        var rule = new MailRule { FromDomain = "@customer.example" };
        Assert.True(MailRuleMatch.Matches(rule, "", "ops@customer.example", "SI"));
    }

    [Fact]
    public void 多条件是与关系()
    {
        var rule = new MailRule { FromDomain = "maersk.com", SubjectContains = "SO" };

        Assert.True(MailRuleMatch.Matches(rule, null, "a@maersk.com", "SO release"));
        Assert.False(MailRuleMatch.Matches(rule, null, "a@maersk.com", "Invoice"));
        Assert.False(MailRuleMatch.Matches(rule, null, "a@example.com", "SO release"));
    }

    [Fact]
    public void 无条件规则不命中任何邮件()
    {
        var rule = new MailRule();
        Assert.False(MailRuleMatch.Matches(rule, null, "a@example.com", "anything"));
    }

    [Fact]
    public void 打标签动作的展示文本包含标签名()
    {
        var rule = new MailRule { Action = MailRuleAction.AddTag, ActionArg = "船司" };
        Assert.Equal("打标签 “船司”", rule.ActionDisplay);
    }
}

/// <summary>规则新增字段（发件域 / 动作参数）的持久化往返。</summary>
[Trait("Category", "Integration")]
public sealed class MailRuleRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteMailRuleRepository _repository;

    public MailRuleRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-rules-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(factory).Initialize();
        _repository = new SqliteMailRuleRepository(factory);
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

    [Fact]
    public async Task 发件域与标签参数_保存后能读回()
    {
        var id = await _repository.UpsertAsync(new MailRule
        {
            Name = "船司自动标签",
            FromDomain = "maersk.com",
            Action = MailRuleAction.AddTag,
            ActionArg = "船司",
            ContinueAfterMatch = true
        });

        var stored = await _repository.GetAsync(id);
        Assert.NotNull(stored);
        Assert.Equal("maersk.com", stored!.FromDomain);
        Assert.Equal(MailRuleAction.AddTag, stored.Action);
        Assert.Equal("船司", stored.ActionArg);
        Assert.True(stored.ContinueAfterMatch);
        Assert.Equal("发件域含 “maersk.com”", stored.ConditionDisplay);

        stored.FromDomain = "cmacgm.com";
        stored.ActionArg = "船司-达飞";
        await _repository.UpsertAsync(stored);

        var updated = await _repository.GetAsync(id);
        Assert.Equal("cmacgm.com", updated!.FromDomain);
        Assert.Equal("船司-达飞", updated.ActionArg);
    }
}
