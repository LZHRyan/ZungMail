using System.Text.Json;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZungMail.Infrastructure.Configuration;

/// <summary>邮件模板持久化（JSON 文件，FR-08）。首次运行写入内置模板。</summary>
public sealed class JsonTemplateStore : ITemplateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly ILogger<JsonTemplateStore> _logger;

    public JsonTemplateStore(IAppPaths paths, ILogger<JsonTemplateStore>? logger = null)
    {
        _path = paths.TemplatesPath;
        _logger = logger ?? NullLogger<JsonTemplateStore>.Instance;
    }

    /// <summary>快速文本预置分组，设置页的分组候选与撰写窗口的分类下拉共用。</summary>
    public static IReadOnlyList<string> QuickTextCategories { get; } = new[]
    {
        "常用语", "单证", "费用", "异常"
    };

    /// <summary>存量安装补种快速文本的标记文件后缀；写过一次后不再自动补种。</summary>
    private const string QuickTextSeedMarkerSuffix = ".quicktext-seeded";

    public string? LoadWarning { get; private set; }

    public async Task<IReadOnlyList<MailTemplate>> GetAllAsync(CancellationToken ct = default)
    {
        LoadWarning = null;

        if (!File.Exists(_path))
        {
            var builtins = CreateSeededTemplates();
            await SaveAsync(builtins, ct).ConfigureAwait(false);
            MarkQuickTextsSeeded();
            return builtins;
        }

        try
        {
            List<MailTemplate> list;
            // 先读完再关闭句柄：File.OpenRead 默认 FileShare.Read，
            // 句柄未释放时后续 SaveAsync 的 File.Move 会被拒绝。
            await using (var stream = File.OpenRead(_path))
            {
                var loaded = await JsonSerializer.DeserializeAsync<List<MailTemplate>>(stream, Options, ct)
                    .ConfigureAwait(false);
                list = loaded ?? new List<MailTemplate>();
            }

            var changed = Normalize(list);
            changed |= MigrateBuiltinFlags(list);
            changed |= EnsureQuickTexts(list);
            if (changed) await SaveAsync(list, ct).ConfigureAwait(false);
            return list;
        }
        catch (JsonException ex)
        {
            // 文件损坏：改名保留原文（便于用户手工找回），再回退到内置模板并重建文件。
            var backup = BackupCorruptFile();
            _logger.LogWarning(ex, "模板文件损坏，已回退内置模板；备份：{Backup}", backup ?? "（备份失败）");

            LoadWarning = backup is null
                ? "模板文件已损坏，已恢复内置模板；原文件未能备份。"
                : $"模板文件已损坏，已备份为 {Path.GetFileName(backup)}，并恢复内置模板。";

            var builtins = CreateSeededTemplates();
            await SaveAsync(builtins, ct).ConfigureAwait(false);
            MarkQuickTextsSeeded();
            return builtins;
        }
    }

    public async Task SaveAsync(IReadOnlyList<MailTemplate> templates, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var copy = templates.Select(NormalizeCopy).ToList();
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, copy, Options, ct).ConfigureAwait(false);
        }
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>内置模板（每次返回全新实例，调用方可安全修改）。</summary>
    public static IReadOnlyList<MailTemplate> CreateBuiltinTemplates() => new List<MailTemplate>
    {
        new()
        {
            Id = "builtin-weekly",
            Name = "周报",
            Category = "工作汇报",
            Description = "每周进展、下周计划的标准汇报结构",
            IsBuiltIn = true,
            Subject = "本周工作周报（{日期}）",
            Body = "<p>各位好：</p><p>本周主要进展如下：</p><ol><li></li><li></li></ol>" +
                   "<p>下周计划：</p><ol><li></li></ol><p>如需协调请随时回复。</p>"
        },
        new()
        {
            Id = "builtin-meeting",
            Name = "会议邀请",
            Category = "会议协作",
            Description = "含时间、地点与议题的会议通知",
            IsBuiltIn = true,
            Subject = "会议邀请：{主题}",
            Body = "<p>各位好：</p><p>拟定于 <strong>{时间}</strong> 召开「{主题}」会议，地点：{地点}。</p>" +
                   "<p>议题：</p><ul><li></li></ul><p>请准时参加，谢谢。</p>"
        },
        new()
        {
            Id = "builtin-review",
            Name = "评审收尾",
            Category = "会议协作",
            Description = "评审结论与待办跟进的标准收尾邮件",
            IsBuiltIn = true,
            Subject = "评审结论与待办跟进",
            Body = "<p>各位好：</p><p>本次评审结论如下：</p><ul><li>结论一</li><li>结论二</li></ul>" +
                   "<p>待办事项与负责人：</p><ol><li></li></ol><p>请在期前回复进度，谢谢。</p>"
        }
    };

    /// <summary>
    /// 快速文本预置话术：货代高频短句，双击即追加到正文；用户可在设置页改写或删除。
    /// 与内置模板分开返回，<see cref="CreateBuiltinTemplates"/> 仍只有整封模板。
    /// </summary>
    public static IReadOnlyList<MailTemplate> CreateBuiltinQuickTexts() => new List<MailTemplate>
    {
        Quick("builtin-quick-received", "收到即处理", "常用语", "收到，马上处理。"),
        Quick("builtin-quick-attachment", "请查收附件", "常用语", "请查收附件。"),
        Quick("builtin-quick-revised", "已按要求修改", "常用语", "已按您的要求修改，请复核。"),
        Quick("builtin-quick-confirm", "请确认内容", "常用语", "您好，麻烦确认以下内容，谢谢！"),
        Quick("builtin-quick-si", "催提单补料", "单证", "请提供提单补料（SI），谢谢。"),
        Quick("builtin-quick-customs", "随附报关资料", "单证", "随函附上报关资料，请查收。"),
        Quick("builtin-quick-quote", "报价有效期", "费用", "报价详见附件，有效期 7 天。"),
        Quick("builtin-quick-payment", "费用已确认", "费用", "费用已确认，请安排付款，谢谢。"),
        Quick("builtin-quick-delay", "延误通知", "异常", "该票货物出现延误，我们正在协调，稍后同步最新进展。"),
        Quick("builtin-quick-container", "箱号核实", "异常", "箱号有误，请核实后重新提供。")
    };

    private static MailTemplate Quick(string id, string name, string category, string body) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Description = "",
        Subject = "",
        Body = body,
        IsBuiltIn = false,
        IsQuickText = true
    };

    /// <summary>首次运行 / 文件损坏重建时写入的完整默认集合：整封模板 + 快速文本。</summary>
    private static List<MailTemplate> CreateSeededTemplates()
    {
        var templates = new List<MailTemplate>(CreateBuiltinTemplates());
        templates.AddRange(CreateBuiltinQuickTexts());
        return templates;
    }

    /// <summary>
    /// 存量安装（模板文件已存在）首次读到新版本时补种快速文本；
    /// 标记文件写过后不再补种，用户删空的快速文本不会被重新塞回来。
    /// </summary>
    private bool EnsureQuickTexts(List<MailTemplate> list)
    {
        if (File.Exists(_path + QuickTextSeedMarkerSuffix)) return false;

        var changed = false;
        foreach (var quick in CreateBuiltinQuickTexts())
        {
            if (list.Any(t => string.Equals(t.Id, quick.Id, StringComparison.Ordinal))) continue;
            list.Add(quick);
            changed = true;
        }

        MarkQuickTextsSeeded();
        return changed;
    }

    private void MarkQuickTextsSeeded()
    {
        try
        {
            File.WriteAllText(_path + QuickTextSeedMarkerSuffix, "v1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 标记写失败只会导致下次启动重复补种；按 Id 去重，不会产生重复条目
            _logger.LogWarning(ex, "写入快速文本补种标记失败");
        }
    }

    /// <summary>补齐缺失字段；返回是否发生了修改。</summary>
    private static bool Normalize(List<MailTemplate> list)
    {
        var changed = false;
        foreach (var template in list)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
            {
                template.Id = Guid.NewGuid().ToString("N");
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(template.Category))
            {
                template.Category = MailTemplate.DefaultCategory;
                changed = true;
            }
            if (template.UpdatedUtc == default)
            {
                template.UpdatedUtc = DateTimeOffset.UtcNow;
                changed = true;
            }
            if (template.Name is null)
            {
                template.Name = string.Empty;
                changed = true;
            }
            if (template.Subject is null)
            {
                template.Subject = string.Empty;
                changed = true;
            }
            if (template.Body is null)
            {
                template.Body = string.Empty;
                changed = true;
            }
            if (template.Description is null)
            {
                template.Description = string.Empty;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// 旧版本文件没有 <see cref="MailTemplate.IsBuiltIn"/> 标记：
    /// 按名称把内置模板认领回来，保留原有 Id 与用户改动。
    /// </summary>
    private static bool MigrateBuiltinFlags(List<MailTemplate> list)
    {
        if (list.Count == 0 || list.Any(t => t.IsBuiltIn)) return false;

        var changed = false;
        foreach (var builtin in CreateBuiltinTemplates())
        {
            var match = list.FirstOrDefault(t =>
                string.Equals((t.Name ?? string.Empty).Trim(), builtin.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            match.IsBuiltIn = true;
            changed = true;
        }
        return changed;
    }

    private static MailTemplate NormalizeCopy(MailTemplate source)
    {
        var copy = source.Clone();
        if (string.IsNullOrWhiteSpace(copy.Id)) copy.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(copy.Category)) copy.Category = MailTemplate.DefaultCategory;
        if (copy.UpdatedUtc == default) copy.UpdatedUtc = DateTimeOffset.UtcNow;
        copy.Name ??= string.Empty;
        copy.Subject ??= string.Empty;
        copy.Body ??= string.Empty;
        copy.Description ??= string.Empty;
        return copy;
    }

    /// <summary>把损坏的模板文件改名保留，避免下一次保存直接覆盖掉用户数据；失败时返回 null。</summary>
    private string? BackupCorruptFile()
    {
        var backup = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";

        try
        {
            File.Move(_path, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份失败不阻断启动：仍回退到内置模板。
            _logger.LogWarning(ex, "备份损坏的模板文件失败");
            return null;
        }
    }
}
