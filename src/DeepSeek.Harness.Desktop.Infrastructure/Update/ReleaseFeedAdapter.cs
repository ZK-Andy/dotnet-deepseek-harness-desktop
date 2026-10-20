namespace DeepSeek.Harness.Desktop.Infrastructure.Update;

/// <summary>
/// release feed 适配器（Core 端口 <see cref="IReleaseFeed"/> 的 Infrastructure 实现）：
/// 抓取最新 release 元数据（版本比对在 Core 的 <see cref="ReleaseMeta"/>，HTTP 抓取在此）。
/// </summary>
/// <param name="log">日志回调（可选；与协调器同款 host.log 行文出口）。</param>
public sealed class ReleaseFeedAdapter(Action<string>? log = null) : IReleaseFeed
{
    /// <inheritdoc/>
    public Task<ReleaseMeta?> FetchLatestAsync(UpdateOptions options, string rid, string? packageKind, CancellationToken cancellationToken) =>
        new ReleaseMetaClient(UpdateHttpClient.Shared, options, log).FetchLatestAsync(rid, packageKind, cancellationToken);
}
