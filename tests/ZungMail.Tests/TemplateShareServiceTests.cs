using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 模板 / 快速文本的分享：导出 JSON → 导入解析 → 冲突重命名。
/// 分享文件带格式标识与版本号，导入端要能挡住「不是模板文件」「版本过新」「空文件」。
/// </summary>
[Trait("Category", "Unit")]
public sealed class TemplateShareServiceTests
{
    private static MailTemplate Template(string name, bool quick = false, string body = "<p>正文</p>")
        => new()
        {
            Name = name,
            Subject = quick ? "" : "主题",
            Body = body,
            Description = "说明",
            Category = quick ? "常用语" : "通用",
            IsBuiltIn = true,
            IsQuickText = quick
        };

    [Fact]
    public void SerializeAndParse_保留两类与字段()
    {
        var json = TemplateShareService.Serialize(new[]
        {
            Template("报价模板"),
            Template("收到即处理", quick: true, body: "收到，马上处理。")
        });

        var parsed = TemplateShareService.Parse(json);

        Assert.True(parsed.Ok, parsed.Message);
        Assert.Equal(1, parsed.TemplateCount);
        Assert.Equal(1, parsed.QuickTextCount);
        Assert.Equal("报价模板", parsed.Entries[0].Name);
        Assert.False(parsed.Entries[0].IsQuickText);
        Assert.True(parsed.Entries[1].IsQuickText);
        Assert.Equal("收到，马上处理。", parsed.Entries[1].Body);
    }

    [Fact]
    public void Serialize_忽略没有名字的条目()
    {
        var json = TemplateShareService.Serialize(new[]
        {
            Template("有名字"),
            Template("   ")
        });

        var parsed = TemplateShareService.Parse(json);

        Assert.Single(parsed.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_空文件被拒绝(string json)
    {
        var parsed = TemplateShareService.Parse(json);

        Assert.False(parsed.Ok);
        Assert.Contains("空", parsed.Message);
    }

    [Fact]
    public void Parse_非JSON被拒绝()
    {
        var parsed = TemplateShareService.Parse("{ 这不是 JSON");

        Assert.False(parsed.Ok);
        Assert.Contains("JSON", parsed.Message);
    }

    [Fact]
    public void Parse_不是本应用的文件被拒绝()
    {
        var parsed = TemplateShareService.Parse("""{ "format": "other.app", "templates": [] }""");

        Assert.False(parsed.Ok);
        Assert.Contains("ZungMail", parsed.Message);
    }

    [Fact]
    public void Parse_旧版本导出的模板文件仍可导入()
    {
        // 改名（MailClient → ZungMail）前导出的文件格式标识是 mailclient.templates
        var parsed = TemplateShareService.Parse(
            """{ "format": "mailclient.templates", "version": 1, "app": "MailClient", "templates": [ { "name": "旧报价模板", "subject": "报价", "body": "<p>正文</p>" } ] }""");

        Assert.True(parsed.Ok, parsed.Message);
        Assert.Equal("旧报价模板", Assert.Single(parsed.Entries).Name);
    }

    [Fact]
    public void Parse_版本过新被拒绝()
    {
        var parsed = TemplateShareService.Parse(
            $$"""{ "format": "zungmail.templates", "version": {{TemplateShareService.CurrentVersion + 1}}, "templates": [ { "name": "x" } ] }""");

        Assert.False(parsed.Ok);
        Assert.Contains("升级", parsed.Message);
    }

    [Fact]
    public void Parse_没有条目被拒绝()
    {
        var parsed = TemplateShareService.Parse("""{ "format": "zungmail.templates", "version": 1, "templates": [] }""");

        Assert.False(parsed.Ok);
        Assert.Contains("没有", parsed.Message);
    }

    [Fact]
    public void Materialize_重新生成Id并去掉内置标记()
    {
        var parsed = TemplateShareService.Parse(
            TemplateShareService.Serialize(new[] { Template("周报") }));

        var imported = TemplateShareService.Materialize(parsed.Entries, Array.Empty<MailTemplate>());

        var item = Assert.Single(imported);
        Assert.NotEqual("", item.Id);
        Assert.False(item.IsBuiltIn);
        Assert.Equal("周报", item.Name);
        Assert.Equal("主题", item.Subject);
    }

    [Fact]
    public void Materialize_同名自动加序号不覆盖现有模板()
    {
        var existing = new[] { Template("报价模板") };
        var parsed = TemplateShareService.Parse(
            TemplateShareService.Serialize(new[] { Template("报价模板"), Template("报价模板") }));

        var imported = TemplateShareService.Materialize(parsed.Entries, existing);

        Assert.Equal("报价模板 (2)", imported[0].Name);
        Assert.Equal("报价模板 (3)", imported[1].Name);
    }

    [Fact]
    public void Materialize_保持快速文本标记并补默认分组()
    {
        var parsed = TemplateShareService.Parse(TemplateShareService.Serialize(new[]
        {
            Template("收到即处理", quick: true)
        }));
        parsed.Entries[0].Category = "";

        var imported = TemplateShareService.Materialize(parsed.Entries, Array.Empty<MailTemplate>());

        var item = Assert.Single(imported);
        Assert.True(item.IsQuickText);
        Assert.Equal("常用语", item.Category);
    }
}
