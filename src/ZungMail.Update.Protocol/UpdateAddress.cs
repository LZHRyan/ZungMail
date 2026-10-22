namespace ZungMail.Update.Protocol;

/// <summary>
/// 升级服务地址归一化：用户既可以填完整 URL，也可以只填域名 / IP（可带端口与路径前缀）。
/// 只允许 http/https，拒绝查询串与片段，避免把「根地址」写成具体接口后无法拼接。
/// </summary>
public static class UpdateAddress
{
    public const string DefaultScheme = "http";

    public static bool TryNormalize(string? input, out Uri? address, out string error)
    {
        address = null;
        error = "";

        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = "请填写升级服务地址，例如 updates.example.com 或 http://192.168.1.20:8080。";
            return false;
        }

        // 只填域名 / IP 时按 http 处理：内网自托管最常见，https 需要显式声明
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = DefaultScheme + "://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "地址格式不正确，请填写域名、IP 或完整 URL。";
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            error = "只支持 http / https 地址。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "地址缺少主机名或 IP。";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "只填写服务根地址即可，不要带查询参数或 # 片段。";
            return false;
        }

        if (uri.AbsolutePath.Contains("..", StringComparison.Ordinal))
        {
            error = "地址路径不能包含 ..。";
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/'),
            Query = "",
            Fragment = ""
        };
        address = builder.Uri;
        return true;
    }

    /// <summary>仅用于界面展示：去掉协议与末尾斜杠，保留主机与前缀路径。</summary>
    public static string ToDisplay(Uri address)
        => address.GetLeftPart(UriPartial.Path).TrimEnd('/');
}
