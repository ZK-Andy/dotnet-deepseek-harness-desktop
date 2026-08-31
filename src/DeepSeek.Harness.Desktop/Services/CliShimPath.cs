namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// CLI shim 注册的路径层纯逻辑（可单测，与平台 IO 解耦）：PATH 值幂等合并、shell rc 幂等块、
/// 生成 shim 标记识别。平台 IO（Windows 注册表 / rc 落盘）与编排见
/// <see cref="CliShimRegistrar"/>。
/// </summary>
public static class CliShimPath
{
    /// <summary>生成的 shim 首行 <see cref="CliShimBuilder.GeneratedMarker"/>（区分本应用 shim 与用户文件）。</summary>
    private const string GeneratedMarker = CliShimBuilder.GeneratedMarker;

    /// <summary>pnpm store-dir 环境变量名（与 <c>HarnessRuntimeHost.ApplyPnpmWriteDirs</c> 注入的 env 名一致；
    /// ADR pnpm-store-alignment-with-terminal）。生成行与幂等守卫共用本常量防漂移。</summary>
    private const string PnpmStoreDirEnv = "pnpm_config_store_dir";

    /// <summary>pnpm cache-dir 环境变量名；语义同 <see cref="PnpmStoreDirEnv"/>。</summary>
    private const string PnpmCacheDirEnv = "pnpm_config_cache_dir";

    // ------------------------------------------------------------------
    // PATH 值幂等合并（分隔符注入，OS 无关）
    // ------------------------------------------------------------------

    /// <summary>PATH 值（<paramref name="separator"/> 分隔）是否已含 <paramref name="token"/>（先规整目录分隔符）。</summary>
    public static bool PathContainsToken(string pathValue, string token, string separator, bool caseInsensitive)
    {
        string tokenNorm = NormalizeTokenForCompare(token, separator, caseInsensitive);
        foreach (string part in pathValue.Split(separator))
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (NormalizeTokenForCompare(part, separator, caseInsensitive) == tokenNorm)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把 <paramref name="token"/> 追加进 PATH（不存在才加）。空 PATH 直接返回 token；已有则原样返回。</summary>
    public static string MergePathToken(string pathValue, string token, string separator, bool caseInsensitive)
    {
        if (PathContainsToken(pathValue, token, separator, caseInsensitive))
        {
            return pathValue;
        }

        return string.IsNullOrEmpty(pathValue) ? token : pathValue + separator + token;
    }

    private static string NormalizeTokenForCompare(string part, string separator, bool caseInsensitive)
    {
        string norm = part.Trim();
        if (separator == ";")
        {
            norm = norm.TrimEnd('\\');
        }
        else
        {
            norm = norm.TrimEnd('/');
        }

        return caseInsensitive ? norm.ToLowerInvariant() : norm;
    }

    // ------------------------------------------------------------------
    // shell rc 幂等块
    // ------------------------------------------------------------------

    /// <summary>rc 块起始标记。</summary>
    public const string RcBeginMarker = "# >>> deepseek-harness-desktop >>>";

    /// <summary>rc 块结束标记。</summary>
    public const string RcEndMarker = "# <<< deepseek-harness-desktop <<<";

    /// <summary>构造把 <paramref name="binDir"/> 加入 PATH 的 export 块（POSIX shell；幂等标记包裹）。</summary>
    public static string BuildShellExportBlock(string binDir, string separator) =>
        BuildShellExportBlocks(new[] { binDir }, separator);

    /// <summary>构造把一组目录加入 PATH 的 export 块（POSIX shell；幂等标记包裹）——同时暴露 pnpm shim 目录
    /// 与系统全局 node 的 global bin 目录（无系统 node 时由桌面装到系统全局，终端与桌面共用同一份 node/dsh）。
    /// <paramref name="pnpmStoreDir"/>/<paramref name="pnpmCacheDir"/> 非空时，一并把 pnpm store/cache 目录
    /// 导出为环境变量，让终端与桌面共用同一份 pnpm store（见 ADR pnpm-store-alignment-with-terminal）。</summary>
    public static string BuildShellExportBlocks(IEnumerable<string> dirs, string separator, string? pnpmStoreDir = null, string? pnpmCacheDir = null)
    {
        string pathLine = $"export PATH=\"{string.Join(separator, dirs)}{separator}$PATH\"";
        string storeLines = BuildPnpmStoreExport(pnpmStoreDir, pnpmCacheDir);
        return $"""
        {RcBeginMarker}
        # DeepSeek Harness Desktop: add the desktop CLI dirs to PATH.
        {pathLine}
        {storeLines}{RcEndMarker}
        """;
    }

    /// <summary>构造 pnpm store/cache 目录的 export 行（POSIX shell）。两个目录都为空时返回空串（不产生多余空行）。</summary>
    private static string BuildPnpmStoreExport(string? pnpmStoreDir, string? pnpmCacheDir)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(pnpmStoreDir))
        {
            lines.Add($"export {PnpmStoreDirEnv}=\"{pnpmStoreDir}\"");
        }

        if (!string.IsNullOrWhiteSpace(pnpmCacheDir))
        {
            lines.Add($"export {PnpmCacheDirEnv}=\"{pnpmCacheDir}\"");
        }

        return lines.Count == 0 ? string.Empty : string.Join("\n", lines) + "\n";
    }

    /// <summary>在 <paramref name="rcContent"/> 尾部追加 <paramref name="block"/>（若已含块则原样返回，幂等）。</summary>
    public static string EnsureShellRcBlock(string rcContent, string block)
    {
        if (rcContent.Contains(RcBeginMarker, StringComparison.Ordinal) &&
            rcContent.Contains(RcEndMarker, StringComparison.Ordinal))
        {
            return rcContent;
        }

        string trimmed = rcContent.TrimEnd();
        return trimmed.Length == 0 ? block.TrimEnd() + "\n"
            : trimmed + "\n\n" + block.TrimEnd() + "\n";
    }

    /// <summary>
    /// 对已含桌面 rc 块、但整个 rc 缺 pnpm store/cache export 的旧版本块做升级：把期望的 pnpm 行
    /// 补进块内。整文件已含 <c>pnpm_config_store_dir</c> 或 <c>pnpm_config_cache_dir</c>（无论块内块外）
    /// 则原样返回（幂等，防与既有行重复）。这是 <see cref="EnsureShellRcBlock"/> 的对偶——前者保证
    /// "块在"，后者保证"rc 含 ADR pnpm-store-alignment-with-terminal 导出的 pnpm 行"，让前版本生成的块
    /// （只含 PATH）也能升级，而不是被"块已存在即跳过"挡住。块缺失或无标记时原样返回（由
    /// <see cref="EnsureShellRcBlock"/> 追加整块）。
    /// </summary>
    public static string EnsurePnpmDirsInRc(string rcContent, string? pnpmStoreDir, string? pnpmCacheDir)
    {
        if (string.IsNullOrWhiteSpace(pnpmStoreDir) && string.IsNullOrWhiteSpace(pnpmCacheDir))
        {
            return rcContent;
        }

        if (rcContent.Contains(PnpmStoreDirEnv, StringComparison.Ordinal) ||
            rcContent.Contains(PnpmCacheDirEnv, StringComparison.Ordinal))
        {
            return rcContent;
        }

        int begin = rcContent.IndexOf(RcBeginMarker, StringComparison.Ordinal);
        int end = rcContent.IndexOf(RcEndMarker, StringComparison.Ordinal);
        if (begin < 0 || end < 0 || end <= begin)
        {
            return rcContent;
        }

        return rcContent.Insert(end, BuildPnpmStoreExport(pnpmStoreDir, pnpmCacheDir));
    }

    // ------------------------------------------------------------------
    // 生成 shim 识别
    // ------------------------------------------------------------------

    /// <summary>目标是否为本应用生成的 shim（生成标记出现在文件前两行）。</summary>
    public static bool IsGeneratedShim(string? content) =>
        content is not null &&
        content.Lines().Take(2).Any(line => line.Contains(GeneratedMarker, StringComparison.Ordinal));

    private static IEnumerable<string> Lines(this string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }
}
