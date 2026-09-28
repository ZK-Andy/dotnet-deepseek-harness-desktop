namespace DeepSeek.Harness.Desktop.Core.Update;

/// <summary>
/// 更新 feed 端口（R3：接口在 Core、实现在 Infrastructure，ADR update-coordinator-core-port）：
/// release feed 抓取（expanded_assets 页解析与版本比对在 Core 的 <see cref="ReleaseMeta"/>，
/// HTTP 抓取在边界实现）。
/// </summary>
public interface IReleaseFeed
{
    /// <summary>抓取最新 release 元数据；无可识别资产返回 null（由状态机转 up-to-date/error）。</summary>
    Task<ReleaseMeta?> FetchLatestAsync(UpdateOptions options, string rid, string? packageKind, CancellationToken cancellationToken);
}
