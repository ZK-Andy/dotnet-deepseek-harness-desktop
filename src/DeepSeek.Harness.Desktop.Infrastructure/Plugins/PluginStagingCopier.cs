namespace DeepSeek.Harness.Desktop.Infrastructure.Plugins;

/// <summary>
/// 插件事务的 staging 拷贝机械（自 <c>PluginProfileTransaction</c> 拆出，事务本体只留 journal/recover 编排）：
/// 递归目录拷贝 + 符号链接重建/降级。staging 与 active 同卷，后续 rename 原子。
/// </summary>
internal static class PluginStagingCopier
{
    private static readonly string[] s_copiedFileExclusions =
    {
        // 运行时管理文件不属于 profile 内容：staging 副本不携带（探针会在 staging 里另起 dsh web 写自己的）
        ".dsh-web-port",
        ".dsh-pid",
    };

    /// <summary>递归拷贝目录（排除运行时管理文件）。
    /// 符号链接**重建链接本体**（读 LinkTarget 原样重建，悬空链同样保留）——绝不 <c>File.Copy</c>
    /// 穿链复制内容：node_modules 的 .bin 与 pnpm 布局全是链接，悬空残留（包已删、链接在）会令
    /// File.Copy 抛 ENOENT 使整个 staging 拷贝失败、事务管线对任何 stale 链接的 profile 永久卡死
    /// （2026-10-05 实机：.bin/node-which 悬空 → companion 升级 0.0.20→0.0.21 每次启动失败，
    /// ADR transactional-plugin-copy-preserves-symlinks）。
    /// 建链被拒（Windows 缺 SeCreateSymbolicLinkPrivilege，逐条抛 <see cref="UnauthorizedAccessException"/>）
    /// 时降级「有效目标穿链复制 + 悬空链跳过」并 loud 留痕——升级可完成而非每次失败
    /// （ADR config-load-fail-safe-and-symlink-privilege-fallback）；降级穿链遍历带
    /// <paramref name="degradedVisited"/> 环守卫（pnpm 互指目录链接图在无权限环境防无界递归）。</summary>
    /// <param name="sourceDir">源目录。</param>
    /// <param name="targetDir">目标目录。</param>
    /// <param name="log">诊断日志出口。</param>
    /// <param name="degradedVisited">降级穿链已解析目录集合（环守卫；顶层调用传 null 即可，首遇降级自建）。</param>
    /// <returns>降级（未能以链接本体落 staging）的条目数，供调用方汇总留痕。</returns>
    public static int CopyDirectory(string sourceDir, string targetDir, Action<string> log, HashSet<string>? degradedVisited = null)
    {
        Directory.CreateDirectory(targetDir);
        int degraded = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            string target = Path.Combine(targetDir, Path.GetFileName(entry));
            if (PathLinkGuard.IsLink(entry))
            {
                degraded += RecreateLink(entry, target, log, degradedVisited);
                continue;
            }

            if (Directory.Exists(entry))
            {
                degraded += CopyDirectory(entry, target, log, degradedVisited);
            }
            else if (!s_copiedFileExclusions.Contains(Path.GetFileName(entry)))
            {
                File.Copy(entry, target);
            }
        }

        return degraded;
    }

    /// <summary>在 staging 重建一个符号链接：目标串原样保留（相对链接保持相对）。链接类型判定：
    /// <c>Directory.Exists</c> 穿链为真 = 目录链（有效）；为假 = 文件链或悬空链（按文件链重建）。
    /// 悬空判定不得用 <c>File.Exists</c>——.NET 7+ 对悬空符号链接返回 true（lstat 语义），
    /// 必须解析链接目标本体判定。建链无权限（<see cref="UnauthorizedAccessException"/>）时降级：
    /// 目录链/有效文件链穿链复制内容、悬空链跳过，各留一行日志（返回值 = 降级条目数）；
    /// 其他异常（如目标已存在的 <see cref="IOException"/>）上抛 fail loud。internal 仅为直测
    /// 该不变量（同 <see cref="DegradeDirLink"/>）。</summary>
    internal static int RecreateLink(string sourceEntry, string target, Action<string> log, HashSet<string>? degradedVisited = null)
    {
        string? linkTarget = Directory.Exists(sourceEntry)
            ? new DirectoryInfo(sourceEntry).LinkTarget
            : new FileInfo(sourceEntry).LinkTarget;
        if (string.IsNullOrEmpty(linkTarget))
        {
            // 探测与重建之间被并发删除等极端窗口：跳过该条并留痕，不让单条链接拖垮整个拷贝
            log($"[host] 插件事务：链接目标读取为空，staging 跳过（{sourceEntry}）");
            return 0;
        }

        if (Directory.Exists(sourceEntry))
        {
            try
            {
                Directory.CreateSymbolicLink(target, linkTarget);
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return DegradeDirLink(sourceEntry, target, log, degradedVisited);
            }
        }

        string resolved = Path.IsPathRooted(linkTarget)
            ? linkTarget
            : Path.Combine(Path.GetDirectoryName(sourceEntry)!, linkTarget);
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
        {
            log($"[host] 插件事务：悬空链接按文件链重建（{sourceEntry} → {linkTarget}）");
        }

        try
        {
            File.CreateSymbolicLink(target, linkTarget);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return DegradeFileLink(sourceEntry, target, linkTarget, log);
        }
    }

    /// <summary>目录链接建链无权限的降级：穿链复制目录内容（嵌套链接同样走降级路径），返回降级条目数。
    /// 环守卫：已解析目标目录（<c>Path.GetFullPath</c>）已在 <paramref name="degradedVisited"/> 即跳过
    /// 并留痕——pnpm 依赖互指（a↔b）的目录链接图在无权限环境会无界递归栈溢出（2026-10-07 R2 评审）。
    /// internal 仅为直测降级语义——真实触发面（无 SeCreateSymbolicLinkPrivilege 的 Windows）无法在 POSIX CI 注入。</summary>
    internal static int DegradeDirLink(string sourceEntry, string target, Action<string> log, HashSet<string>? degradedVisited = null)
    {
        string? linkTarget = new DirectoryInfo(sourceEntry).LinkTarget;
        string resolved = string.IsNullOrEmpty(linkTarget)
            ? Path.GetFullPath(sourceEntry)
            : Path.GetFullPath(
                Path.IsPathRooted(linkTarget)
                    ? linkTarget
                    : Path.Combine(Path.GetDirectoryName(sourceEntry)!, linkTarget));
        degradedVisited ??= new HashSet<string>(StringComparer.Ordinal);
        if (!degradedVisited.Add(resolved))
        {
            log($"[host] 插件事务：目录链接降级复制遇环（{sourceEntry} → {linkTarget}），跳过防无界递归");
            return 1;
        }

        log($"[host] 插件事务：目录链接建链无权限，降级穿链复制内容（{sourceEntry} → {linkTarget}）");
        return 1 + CopyDirectory(sourceEntry, target, log, degradedVisited);
    }

    /// <summary>文件链接建链无权限的降级：有效目标穿链复制内容（<c>File.Copy</c> 跟随源链接读目标）；
    /// 目标悬空（含目标本身是悬空链——<c>File.Exists</c> lstat 语义探不出）跳过并留痕，不留半拷贝。
    /// 每条路径恰留一行。internal 仅为直测降级语义，同 <see cref="DegradeDirLink"/>。</summary>
    internal static int DegradeFileLink(string sourceEntry, string target, string linkTarget, Action<string> log)
    {
        try
        {
            File.Copy(sourceEntry, target);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            log($"[host] 插件事务：悬空链接建链无权限且无内容可复制，staging 跳过（{sourceEntry} → {linkTarget}）：{ex.Message}");
            return 1;
        }

        log($"[host] 插件事务：文件链接建链无权限，降级穿链复制内容（{sourceEntry} → {target}）");
        return 1;
    }
}
