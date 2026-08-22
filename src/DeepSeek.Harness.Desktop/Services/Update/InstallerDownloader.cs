using System.Security.Cryptography;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>安装包下载器：<c>.part</c> 临时名 + 完成后原子改名 + SHA256SUMS 强校验。</summary>
public sealed class InstallerDownloader
{
    private readonly HttpClient _http;

    /// <summary>创建下载器。HttpClient 由外部持有复用连接池。</summary>
    public InstallerDownloader(HttpClient http) => _http = http;

    /// <summary>
    /// 下载资产到目标目录：先写 <c>&lt;name&gt;.part</c>，完成后改名为最终文件名并校验 SHA-256。
    /// 校验失败删除半成品并抛出（状态机转 Error）。
    /// 跨实例互斥：独占 <c>.download.lock</c>——另一实例正在下载时抛出，防双写损坏半成品
    /// （锁随进程死亡自动释放，无陈锁问题）。
    /// </summary>
    /// <returns>落地的本地文件全路径。</returns>
    public async Task<string> DownloadAsync(ReleaseMeta meta, string destDir, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, meta.AssetName);
        var partPath = destPath + ".part";

        using var lockFs = TryAcquireDownloadLock(destDir)
            ?? throw new InvalidOperationException("另一实例正在下载更新，请稍候后再试");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await using (var target = File.Create(partPath))
        {
            await using var source = await _http.GetStreamAsync(meta.AssetUrl, cts.Token).ConfigureAwait(false);
            await source.CopyToAsync(target, cts.Token).ConfigureAwait(false);
        }

        if (meta.Sha256Url is not null)
        {
            await VerifySha256Async(partPath, meta.AssetName, meta.Sha256Url, cts.Token).ConfigureAwait(false);
        }

        File.Move(partPath, destPath, overwrite: true);
        return destPath;
    }

    /// <summary>下载 SHA256SUMS.txt，找目标文件名的行比对哈希；找不到对应行视为无法校验——放行（记日志由调用方处理）。</summary>
    public async Task VerifySha256Async(string filePath, string assetName, string sha256Url, CancellationToken cancellationToken)
    {
        var sums = await _http.GetStringAsync(sha256Url, cancellationToken).ConfigureAwait(false);
        var expected = ParseSha256(sums, assetName);
        if (expected is null)
        {
            throw new InvalidOperationException($"SHA256SUMS 中无 {assetName} 条目");
        }

        var actual = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(filePath);
            throw new InvalidDataException($"SHA-256 不匹配：{assetName} 期望 {expected} 实际 {actual}");
        }
    }

    /// <summary>解析 SHA256SUMS 文本（格式 <c>&lt;hex&gt;  &lt;name&gt;</c>），返回目标文件的哈希。</summary>
    public static string? ParseSha256(string sumsContent, string assetName)
    {
        foreach (var line in sumsContent.Split('\n'))
        {
            var trimmed = line.Trim();
            var space = trimmed.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }

            var hex = trimmed[..space];
            // 标准格式为「<hex>␣␣<name>」双空格；二进制标记为「<hex>␣*<name>」——两种都接受
            var name = trimmed[(space + 1)..].TrimStart('*', ' ');
            if (name == assetName && hex.Length == 64 && hex.All(Uri.IsHexDigit))
            {
                return hex;
            }
        }

        return null;
    }

    /// <summary>
    /// 尝试独占下载锁（<c>.download.lock</c>，FileShare.None）；被其他进程持有时返回 null。
    /// 锁句柄随进程死亡自动释放，不存在陈锁。
    /// </summary>
    public static FileStream? TryAcquireDownloadLock(string destDir)
    {
        Directory.CreateDirectory(destDir);
        try
        {
            return File.Open(Path.Combine(destDir, ".download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // 被占用 = 他实例下载中
            return null;
        }
    }

    /// <summary>流式计算文件 SHA-256（安装包百 MB 级，不整读内存）。</summary>
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(filePath);
        var bytes = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(bytes);
    }
}
