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
    /// 任何失败（HTTP 非 2xx / 校验文件缺失 / 哈希不匹配 / 超时）都清掉半成品并抛出（状态机转 Error）。
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
        try
        {
            await using (var target = File.Create(partPath))
            {
                // GetStreamAsync 对非 2xx 不抛（404/500 会把错误页当安装包写满 .part，只报误导的校验失败），
                // 必须显式 EnsureSuccessStatusCode
                using var response = await _http
                    .GetAsync(meta.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await source.CopyToAsync(target, cts.Token).ConfigureAwait(false);
            }

            if (meta.Sha256Url is not null)
            {
                await VerifySha256Async(partPath, meta.AssetName, meta.Sha256Url, cts.Token).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException(
                    $"release 未附 SHA256SUMS.txt，拒绝无校验安装：{meta.AssetName}（宁可误报不装坏包）");
            }
        }
        catch
        {
            // 校验/超时/HTTP 错误的半成品一律清除，避免残留 .part 误导后续重试（重复删除无害）
            File.Delete(partPath);
            throw;
        }

        File.Move(partPath, destPath, overwrite: true);
        return destPath;
    }

    /// <summary>下载 SHA256SUMS.txt，找目标文件名的行比对哈希；找不到对应行视为无法校验——fail loud 拒装。</summary>
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
