namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 路径链接判定（只探不穿）：profile 与锁路径在操作前用它拒符号链接
/// （ADR profile-lock-path-symlink-rejection）。
/// </summary>
/// <remarks>
/// 与 <see cref="Runtime.RunMarker"/> 的「解链重建」刻意不同：marker 是无状态取证文件，拔掉重建无损失；
/// profile 是用户数据目录，静默 unlink 会毁掉用户有意建立的指向，故数据路径一律拒操作、不自愈。
/// 折算式独立为纯函数 <see cref="IsLinkFrom"/>，使 Windows 重解析点分支在所有平台可测。
/// </remarks>
internal static class PathLinkGuard
{
    /// <summary>
    /// 路径当前是否为符号链接/重解析点。路径不存在返回 <c>false</c>；探测失败（权限/平台不支持）
    /// 按「非链接」放行**并记一行日志**——守卫不可用必须留痕，静默放行会让拒链在受限目录上无声失效。
    /// </summary>
    /// <param name="path">文件或目录路径。</param>
    /// <returns>是链接/重解析点为 true。</returns>
    public static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            bool exists = File.Exists(path) || Directory.Exists(path);
            // 属性只在路径作为对象存在时读：Unix 下 FileInfo.Attributes 对缺失路径返回
            // (FileAttributes)(-1)，其 ReparsePoint 位为真，误读会把每个不存在的路径判成链接
            return IsLinkFrom(info.LinkTarget, exists ? info.Attributes : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            HostLog.Write($"[host] 链接探测失败，拒链守卫按非链接放行：{path}：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 由探测结果折出链接判定（纯函数）：有链接目标即为链接（悬空链也命中——此时属性不可得），
    /// 否则仅当属性带重解析点才算（Windows junction 等无 <c>LinkTarget</c> 的形态）。
    /// </summary>
    /// <param name="linkTarget"><c>FileSystemInfo.LinkTarget</c> 的值；非链接为 null。</param>
    /// <param name="attributes">路径自身属性；路径不存在时传 null（缺失不得参与判定）。</param>
    /// <returns>应判定为链接为 true。</returns>
    public static bool IsLinkFrom(string? linkTarget, FileAttributes? attributes)
        => linkTarget is not null
           || (attributes is { } attrs && attrs.HasFlag(FileAttributes.ReparsePoint));
}
