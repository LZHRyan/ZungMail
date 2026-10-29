using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>一封邮件的运输字段写入项（箱型箱量 / 航线 / 条款 / 金额）。</summary>
public sealed record ShipmentFieldEntry(long EmailId, ShipmentFields Fields);

/// <summary>
/// 运输字段仓储：保存从邮件里抽出的箱型箱量、航线对、贸易条款与金额，
/// 供列表徽标、详情卡与 <c>size:</c>/<c>lane:</c>/<c>term:</c>/<c>price:</c> 搜索使用。
/// </summary>
public interface IMailShipmentFieldRepository
{
    /// <summary>批量写入（同一事务）；空字段的邮件会被删除记录，避免残留旧值。</summary>
    Task ReplaceRangeAsync(IReadOnlyList<ShipmentFieldEntry> entries, CancellationToken ct = default);

    Task<ShipmentFields> GetForMessageAsync(long emailId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<long, ShipmentFields>> GetForMessagesAsync(
        IReadOnlyList<long> emailIds,
        CancellationToken ct = default);
}
