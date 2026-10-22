using System.Text;

namespace ZungMail.Core.Abstractions;

/// <summary>诊断流程中的单个阶段结果。</summary>
public sealed record DiagnosticsStep(string Name, bool Success, string Detail, long ElapsedMs)
{
    public string StateText => Success ? "通过" : "失败";
}

/// <summary>收发诊断报告：逐阶段结论，可直接导出给同事或服务商排查。</summary>
public sealed class DiagnosticsReport
{
    public required long AccountId { get; init; }
    public required string AccountEmail { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<DiagnosticsStep> Steps { get; } = new();

    public bool Success => Steps.Count > 0 && Steps.All(step => step.Success);

    public string Summary => Success
        ? "全部检查通过"
        : "存在问题：" + string.Join("；", Steps.Where(s => !s.Success).Select(s => s.Name));

    /// <summary>导出为纯文本报告，便于粘贴到工单。</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("ZungMail 收发诊断报告");
        builder.AppendLine($"账户：{AccountEmail}");
        builder.AppendLine($"时间：{GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"结论：{Summary}");
        builder.AppendLine(new string('-', 48));
        foreach (var step in Steps)
        {
            builder.AppendLine($"[{(step.Success ? "OK" : "!!")}] {step.Name} ({step.ElapsedMs} ms)");
            builder.AppendLine("     " + step.Detail);
        }
        return builder.ToString();
    }
}

/// <summary>邮件收发诊断：按 DNS → TCP → TLS/登录 → 文件夹 → SMTP 认证顺序定位问题。</summary>
public interface IMailDiagnosticsService
{
    Task<DiagnosticsReport> RunAsync(long accountId, CancellationToken ct = default);
}
