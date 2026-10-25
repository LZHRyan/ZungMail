namespace ZungMail.Core.Abstractions;

/// <summary>
/// 数据库里的键值元数据（<c>meta</c> 表）：记录「一次性回填已跑过」这类跨启动状态。
/// 与设置文件不同，它跟着数据库走——换机器 / 换数据目录时状态跟着数据一起搬。
/// </summary>
public interface IAppMetadataStore
{
    /// <summary>读取一个键；不存在返回 null。</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>该键是否已写入过（一次性任务的「已跑过」判断）。</summary>
    Task<bool> HasAsync(string key, CancellationToken ct = default);

    /// <summary>写入 / 覆盖一个键。</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);
}
