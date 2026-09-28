namespace DeepSeek.Harness.Desktop.Core.Update;

/// <summary>
/// 安装包下载端口（R3：接口在 Core、实现在 Infrastructure，ADR update-coordinator-core-port）：
/// 下载落地与装前 SHA 复取（校验判据在调用方，HTTP/落盘在边界实现）。
/// </summary>
public interface IPackageDownloader
{
    /// <summary>下载安装包到目标目录并校验，返回本地文件路径。</summary>
    Task<string> DownloadAsync(ReleaseMeta meta, string destDir, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>装前从 release 侧 SHA256SUMS 复取期望哈希（用户空间改写不了的锚点）；离线时抛出拒装。</summary>
    Task<string> FetchExpectedSha256Async(string repository, string version, string assetName, CancellationToken cancellationToken);
}
