namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 老库升级的进度回报：<see cref="Step"/>/<see cref="Total"/> 用于「第 2/3 步」，
/// <see cref="Detail"/> 是给人看的一句话（哪一步、在干什么）。
///
/// 机械盘上重建会话索引实测 163 秒，期间界面什么都没有；
/// 升级进度窗口靠这条信息告诉用户「在动，别关」。
/// </summary>
/// <param name="Step">已完成/正在执行的步骤序号，从 1 开始。</param>
/// <param name="Total">本次升级的总步骤数。</param>
/// <param name="Detail">步骤说明，例如「更新数据结构：会话归并索引（全表重建，最慢的一步）」。</param>
public readonly record struct DatabaseUpgradeProgress(int Step, int Total, string Detail);
