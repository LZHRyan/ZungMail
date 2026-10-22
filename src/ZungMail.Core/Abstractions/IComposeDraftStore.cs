using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>
/// 写信草稿的本地存储（自动保存 / 崩溃恢复）。
/// 一个草稿一个文件，写入是原子的（临时文件 + 替换），读取时跳过损坏文件。
/// </summary>
public interface IComposeDraftStore
{
    /// <summary>按更新时间倒序列出全部草稿。</summary>
    Task<IReadOnlyList<ComposeDraft>> GetAllAsync(CancellationToken ct = default);

    /// <summary>保存（同 Id 覆盖）。</summary>
    Task SaveAsync(ComposeDraft draft, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);
}
