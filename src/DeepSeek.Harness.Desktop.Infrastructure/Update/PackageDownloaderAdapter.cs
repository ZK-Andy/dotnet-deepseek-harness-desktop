namespace DeepSeek.Harness.Desktop.Infrastructure.Update;

/// <summary>
/// 安装包下载适配器（Core 端口 <see cref="IPackageDownloader"/> 的 Infrastructure 实现）：
/// 安装包落地与装前 SHA 复取（校验判据在调用方，HTTP 与落盘在此）。
/// </summary>
/// <param name="log">日志回调（可选；与协调器同款 host.log 行文出口）。</param>
public sealed class PackageDownloaderAdapter(Action<string>? log = null) : IPackageDownloader
{
    /// <summary>下载器 worker（无状态：构造只吃客户端与日志出口）——持有一次复用，不在每次调用里重建。</summary>
    private readonly InstallerDownloader _downloader = new(UpdateHttpClient.Shared, log);

    /// <inheritdoc/>
    public Task<string> DownloadAsync(ReleaseMeta meta, string destDir, TimeSpan timeout, CancellationToken cancellationToken) =>
        _downloader.DownloadAsync(meta, destDir, timeout, cancellationToken);

    /// <inheritdoc/>
    public Task<string> FetchExpectedSha256Async(string repository, string version, string assetName, CancellationToken cancellationToken) =>
        _downloader.FetchSha256Async(repository, version, assetName, cancellationToken);
}
