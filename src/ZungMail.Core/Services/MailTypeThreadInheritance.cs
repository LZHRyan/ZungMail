using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 会话类型继承：同一会话里已有确定类型的邮件，可以给仍未识别的同会话邮件一个「继承类型」。
/// <para>
/// 规则：<b>异常类优先</b>（查验 / 甩柜 / 延误必须先被看见），其次取出现次数最多的类型；
/// 次数相同按类型枚举顺序（异常类本来就在前面）。纯函数，便于单测与在仓储层复用。
/// </para>
/// </summary>
public static class MailTypeThreadInheritance
{
    /// <summary>从「类型 → 次数」里选继承类型；没有任何确定类型时返回 <see cref="MailType.Unknown"/>。</summary>
    public static MailType PickWinner(IEnumerable<KeyValuePair<MailType, int>> counts)
    {
        var best = MailType.Unknown;
        var bestCount = 0;
        var bestIsException = false;

        foreach (var (type, count) in counts)
        {
            if (type == MailType.Unknown || count <= 0) continue;

            var isException = MailTypes.IsException(type);
            if (best == MailType.Unknown
                || (isException && !bestIsException)
                || (isException == bestIsException && count > bestCount))
            {
                best = type;
                bestCount = count;
                bestIsException = isException;
            }
        }

        return best;
    }
}
