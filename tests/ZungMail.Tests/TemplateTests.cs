using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Configuration;
using Xunit;

namespace ZungMail.Tests;

/// <summary>模板保存前校验：字段级错误 / 提示与危险标记检测。</summary>
[Trait("Category", "Unit")]
public sealed class TemplateValidatorTests
{
    private static MailTemplate ValidTemplate() => new()
    {
        Id = "t1",
        Name = "周报",
        Subject = "本周工作（{日期}）",
        Body = "<p>本周进展：</p><ol><li>事项一</li></ol>"
    };

    [Fact]
    public void Validate_合法模板_无错误无提示()
    {
        var result = TemplateValidator.Validate(ValidTemplate());

        Assert.True(result.IsValid);
        Assert.False(result.HasIssues);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Validate_名称为空_报错并定位到Name字段()
    {
        var template = ValidTemplate();
        template.Name = "   ";

        var result = TemplateValidator.Validate(template);

        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorFor(nameof(MailTemplate.Name)));
        Assert.Equal("请输入模板名称", result.ErrorSummary);
    }

    [Fact]
    public void Validate_名称超长_报错()
    {
        var template = ValidTemplate();
        template.Name = new string('名', TemplateValidator.MaxNameLength + 1);

        var result = TemplateValidator.Validate(template);

        Assert.False(result.IsValid);
        Assert.Contains("不能超过", result.ErrorFor(nameof(MailTemplate.Name)));
    }

    [Fact]
    public void Validate_重名报错_但忽略自身()
    {
        var template = ValidTemplate();
        var other = ValidTemplate();
        other.Id = "t2";

        Assert.False(TemplateValidator.Validate(template, new[] { other }).IsValid);

        var same = ValidTemplate();
        Assert.True(TemplateValidator.Validate(template, new[] { same }).IsValid);
    }

    [Fact]
    public void Validate_主题为空_仅提示不阻止保存()
    {
        var template = ValidTemplate();
        template.Subject = "";

        var result = TemplateValidator.Validate(template);

        Assert.True(result.IsValid);
        Assert.NotNull(result.WarningFor(nameof(MailTemplate.Subject)));
    }

    [Fact]
    public void Validate_正文为空_仅提示不阻止保存()
    {
        var template = ValidTemplate();
        template.Body = "";

        var result = TemplateValidator.Validate(template);

        Assert.True(result.IsValid);
        Assert.NotNull(result.WarningFor(nameof(MailTemplate.Body)));
    }

    [Fact]
    public void Validate_正文含脚本_报错且给出可读提示()
    {
        var template = ValidTemplate();
        template.Body = "<p>正文</p><script>alert(1)</script>";

        var result = TemplateValidator.Validate(template);

        Assert.False(result.IsValid);
        Assert.Contains("script", result.ErrorFor(nameof(MailTemplate.Body)));
    }

    [Theory]
    [InlineData("<script>x</script>", true)]
    [InlineData("<iframe src=\"https://a\"></iframe>", true)]
    [InlineData("<a href=\"javascript:void(0)\">x</a>", true)]
    [InlineData("<p onclick=\"x()\">x</p>", true)]
    [InlineData("<p>普通正文</p>", false)]
    [InlineData("", false)]
    public void FindDangerousMarkup_识别四类风险标记(string html, bool dangerous)
        => Assert.Equal(dangerous, TemplateValidator.FindDangerousMarkup(html) is not null);
}

/// <summary>模板占位符：自动取值、替换、未填检测与预览高亮。</summary>
[Trait("Category", "Unit")]
public sealed class TemplatePlaceholderTests
{
    [Fact]
    public void AutoValues_按给定时间生成时间日期与周次()
    {
        var values = TemplatePlaceholder.AutoValues(new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.FromHours(8)));

        Assert.Equal("2026-10-17 09:30", values["{时间}"]);
        Assert.Equal("2026-10-17", values["{日期}"]);
        Assert.StartsWith("第 ", values["{第N周}"]);
    }

    [Fact]
    public void Resolve_替换已填占位符_未填保持原样()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["{主题}"] = "季度评审",
            ["{地点}"] = null
        };

        var result = TemplatePlaceholder.Resolve("会议：{主题}，地点：{地点}", values);

        Assert.Equal("会议：季度评审，地点：{地点}", result);
    }

    [Fact]
    public void Unresolved_按定义顺序去重()
    {
        var pending = TemplatePlaceholder.Unresolved("{地点}{主题}{地点}");

        Assert.Equal(new[] { "{主题}", "{地点}" }, pending.Select(p => p.Token));
    }

    [Fact]
    public void HighlightUnresolved_只包裹标签外文本()
    {
        const string html = "<p style=\"color:{主题}\">{主题}</p>";

        var result = TemplatePlaceholder.HighlightUnresolved(html);

        // 标签内的令牌保持原样，文本节点被包成高亮 span
        Assert.Contains("color:{主题}", result);
        Assert.Contains("<span", result);
        Assert.Contains("</span>", result);
    }

    [Fact]
    public void HighlightUnresolved_无占位符时原样返回()
    {
        const string html = "<p>没有占位符</p>";
        Assert.Equal(html, TemplatePlaceholder.HighlightUnresolved(html));
    }

    [Fact]
    public void Apply_生成替换后的副本且不改原名与分组()
    {
        var template = new MailTemplate
        {
            Name = "会议邀请",
            Category = "会议协作",
            Subject = "会议邀请：{主题}",
            Body = "<p>地点：{地点}</p>"
        };

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["{主题}"] = "方案评审",
            ["{地点}"] = "三楼会议室"
        };

        var applied = TemplatePlaceholder.Apply(template, values);

        Assert.Equal("会议邀请：方案评审", applied.Subject);
        Assert.Equal("<p>地点：三楼会议室</p>", applied.Body);
        Assert.Equal("会议邀请", applied.Name);
        Assert.Equal("会议协作", applied.Category);
        Assert.Equal("会议邀请：{主题}", template.Subject); // 原模板不被修改
    }
}

/// <summary>
/// 预览与套用链路回归（第 7 条验收）：三类内置模板都要能通过校验、
/// 走完「占位符替换 → 未填高亮 → 组装 HTML 文档」的预览管线，并且套用结果不再残留占位符。
/// </summary>
[Trait("Category", "Unit")]
public sealed class TemplatePreviewPipelineTests
{
    public static IEnumerable<object[]> BuiltinIds =>
        JsonTemplateStore.CreateBuiltinTemplates().Select(t => new object[] { t.Id });

    [Theory]
    [MemberData(nameof(BuiltinIds))]
    public void 内置模板_通过校验并能渲染预览文档(string id)
    {
        var templates = JsonTemplateStore.CreateBuiltinTemplates();
        var template = Assert.Single(templates, t => t.Id == id);

        var validation = TemplateValidator.Validate(template, templates);
        Assert.True(validation.IsValid, validation.ErrorSummary);

        var autoValues = TemplatePlaceholder.AutoValues(DateTimeOffset.Now)
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        var resolved = TemplatePlaceholder.Resolve(template.Body, autoValues);
        var highlighted = TemplatePlaceholder.HighlightUnresolved(resolved);
        var document = HtmlMailDocumentBuilder.Build(highlighted, new MailDocumentOptions { AllowRemoteImages = true });

        Assert.Contains("<html", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</html>", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", document, StringComparison.OrdinalIgnoreCase);

        // 时间 / 日期 / 周次 自动填充；剩余占位符按有无分别高亮，便于用户在预览中定位
        Assert.DoesNotContain("{时间}", resolved);
        Assert.DoesNotContain("{日期}", resolved);
        if (TemplatePlaceholder.Unresolved(resolved).Count > 0)
        {
            Assert.Contains("<span", highlighted);
        }
        else
        {
            Assert.DoesNotContain("<span", highlighted);
        }
    }

    [Theory]
    [MemberData(nameof(BuiltinIds))]
    public void 内置模板_填全占位符后套用不再残留令牌(string id)
    {
        var templates = JsonTemplateStore.CreateBuiltinTemplates();
        var template = Assert.Single(templates, t => t.Id == id);

        var values = TemplatePlaceholder.All.ToDictionary(
            token => token.Token,
            token => (string?)"示例",
            StringComparer.Ordinal);
        values["{第N周}"] = "第 42 周";

        var applied = TemplatePlaceholder.Apply(template, values);

        Assert.DoesNotContain("{", applied.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("{", applied.Body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(applied.Subject));
        Assert.False(string.IsNullOrWhiteSpace(applied.Body));
    }
}

/// <summary>快速文本预置：分组固定、正文非空、能通过模板校验，并且不混进整封模板集合。</summary>
[Trait("Category", "Unit")]
public sealed class QuickTextPresetTests
{
    [Fact]
    public void 预置快速文本_分组与正文完整且通过校验()
    {
        var quickTexts = JsonTemplateStore.CreateBuiltinQuickTexts();

        Assert.NotEmpty(quickTexts);
        Assert.Equal(quickTexts.Count, quickTexts.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(quickTexts, q => Assert.Contains(q.Category, JsonTemplateStore.QuickTextCategories));
        Assert.All(quickTexts, q => Assert.False(string.IsNullOrWhiteSpace(q.Name)));
        Assert.All(quickTexts, q => Assert.False(string.IsNullOrWhiteSpace(q.Body)));
        Assert.All(quickTexts, q => Assert.True(q.IsQuickText));
        Assert.All(quickTexts, q => Assert.False(q.IsBuiltIn)); // 允许用户删除自己不需要的话术

        var all = JsonTemplateStore.CreateBuiltinTemplates().Concat(quickTexts).ToList();
        Assert.All(quickTexts, q => Assert.True(TemplateValidator.Validate(q, all).IsValid));
    }

    [Fact]
    public void 整封模板集合_不含快速文本()
        => Assert.DoesNotContain(JsonTemplateStore.CreateBuiltinTemplates(), t => t.IsQuickText);

    [Fact]
    public void 克隆模板_保留快速文本标记()
    {
        var quick = JsonTemplateStore.CreateBuiltinQuickTexts()[0];

        Assert.True(quick.Clone().IsQuickText);
        Assert.False(quick.Clone(newId: true).IsBuiltIn);
        Assert.True(quick.Clone(newId: true).IsQuickText);
    }
}

/// <summary>模板 JSON 持久化：内置模板、往返读写、损坏文件回退与旧字段归一化。</summary>
[Trait("Category", "Integration")]
public sealed class JsonTemplateStoreTests : IDisposable
{
    private readonly string _root;

    public JsonTemplateStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task GetAllAsync_首次读取_写入三套内置模板与快速文本()
    {
        var store = CreateStore();

        var templates = await store.GetAllAsync();

        var full = templates.Where(t => !t.IsQuickText).ToList();
        Assert.Equal(3, full.Count);
        Assert.All(full, t => Assert.True(t.IsBuiltIn));
        Assert.Contains(full, t => t.Name == "周报");

        var quick = templates.Where(t => t.IsQuickText).ToList();
        Assert.Equal(JsonTemplateStore.CreateBuiltinQuickTexts().Count, quick.Count);
        Assert.All(quick, t => Assert.False(string.IsNullOrWhiteSpace(t.Body)));
        Assert.True(File.Exists(Paths.TemplatesPath));
    }

    [Fact]
    public async Task GetAllAsync_存量文件_补种快速文本且只补一次()
    {
        var store = CreateStore();
        // 旧版本留下的模板文件：没有快速文本，也没有补种标记
        await store.SaveAsync(new[]
        {
            new MailTemplate { Name = "旧模板", Body = "<p>旧内容</p>" }
        });

        var first = await store.GetAllAsync();
        var seeded = first.Where(t => t.IsQuickText).ToList();
        Assert.Equal(JsonTemplateStore.CreateBuiltinQuickTexts().Count, seeded.Count);

        // 标记文件写过之后，用户删空快速文本不会被重新塞回来
        await store.SaveAsync(first.Where(t => !t.IsQuickText).ToList());
        var second = await store.GetAllAsync();

        Assert.DoesNotContain(second, t => t.IsQuickText);
        Assert.Contains(second, t => t.Name == "旧模板");
    }

    [Fact]
    public async Task SaveAsync_往返读写保留全部字段()
    {
        var store = CreateStore();
        var template = new MailTemplate
        {
            Name = "客户回访",
            Subject = "回访：{姓名}",
            Body = "<p>您好 {姓名}</p>",
            Description = "季度回访",
            Category = "客户沟通",
            IsBuiltIn = false
        };

        await store.SaveAsync(new[] { template });
        var loaded = await store.GetAllAsync();

        var one = Assert.Single(loaded, t => !t.IsQuickText);
        Assert.Equal(template.Id, one.Id);
        Assert.Equal("客户回访", one.Name);
        Assert.Equal("回访：{姓名}", one.Subject);
        Assert.Equal("季度回访", one.Description);
        Assert.Equal("客户沟通", one.Category);
        Assert.False(one.IsBuiltIn);
    }

    [Fact]
    public async Task GetAllAsync_文件损坏_改名备份并回退内置模板()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(Paths.TemplatesPath, "{ 这不是合法 JSON");

        var templates = await store.GetAllAsync();

        Assert.Equal(3, templates.Count(t => !t.IsQuickText));

        var backup = Assert.Single(Directory.GetFiles(_root, "*.corrupt-*.json"));
        Assert.Equal("{ 这不是合法 JSON", await File.ReadAllTextAsync(backup));

        Assert.NotNull(store.LoadWarning);
        Assert.Contains(Path.GetFileName(backup), store.LoadWarning);
    }

    [Fact]
    public async Task GetAllAsync_旧文件缺字段_读取时自动补齐()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(Paths.TemplatesPath,
            "[{\"Id\":\"legacy\",\"Name\":\"周报\",\"Subject\":\"\",\"Body\":\"\"}]");

        var templates = await store.GetAllAsync();

        var legacy = Assert.Single(templates, t => !t.IsQuickText);
        Assert.Equal(MailTemplate.DefaultCategory, legacy.Category);
        Assert.Equal("", legacy.Description);
        Assert.NotEqual(default, legacy.UpdatedUtc);
        Assert.True(legacy.IsBuiltIn); // 按名称认领为内置模板
    }

    private FakePaths Paths => new(_root);

    private JsonTemplateStore CreateStore()
    {
        var paths = Paths;
        paths.EnsureCreated();
        return new JsonTemplateStore(paths);
    }

    /// <summary>只提供模板路径的最小 IAppPaths 实现。</summary>
    private sealed class FakePaths : IAppPaths
    {
        public FakePaths(string root) => RootDirectory = root;

        public string RootDirectory { get; }
        public string DatabasePath => Path.Combine(RootDirectory, "mail.db");
        public string TemplatesPath => Path.Combine(RootDirectory, "templates.json");
        public string SettingsPath => Path.Combine(RootDirectory, "settings.json");
        public string LogDirectory => Path.Combine(RootDirectory, "logs");
        public string EmlDirectory => Path.Combine(RootDirectory, "eml");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(EmlDirectory);
        }
    }
}
