namespace DeepSeek.Harness.Desktop.Services;

/// <summary>运行时定位：优先探测随应用捆绑的 DSH 运行时（resources/runtime），否则回退 PATH 里的 dsh。</summary>
public static class RuntimeLocator
{
    /// <summary>捆绑运行时目录覆盖环境变量。</summary>
    public const string BundledDirEnv = "DSH_DESKTOP_RUNTIME_DIR";

    /// <summary>引导下载运行时的落位目录覆盖环境变量（dev/测试用；ADR online-first-unbundled-runtime）。</summary>
    public const string DownloadDirEnv = "DSH_DESKTOP_RUNTIME_DOWNLOAD_DIR";

    /// <summary>解析捆绑运行时目录：优先环境变量 <see cref="BundledDirEnv"/>，否则应用目录下 resources/runtime。</summary>
    public static string ResolveRuntimeDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(BundledDirEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return Path.Combine(AppContext.BaseDirectory, "resources", "runtime");
    }

    /// <summary>
    /// 解析引导下载运行时的落位目录：优先 <see cref="DownloadDirEnv"/>，否则用户主目录下
    /// <c>~/.dsh-desktop/runtime</c>（桌面自有目录，不与生态共享 home <c>~/.dsh</c> 混放）。
    /// </summary>
    public static string ResolveDownloadedRuntimeDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(DownloadDirEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh-desktop",
            "runtime");
    }

    /// <summary>解析 node 可执行的实际文件名：Windows 发行包为 node.exe（File.Exists 不做扩展名探测，
    /// 此处显式双查——缺失该分支时 Windows 捆绑运行时永远定位不到，属潜伏 bug 已修复）。</summary>
    private static string? TryFindNodeFile(string runtimeDir)
    {
        string node = Path.Combine(runtimeDir, "node");
        if (File.Exists(node))
        {
            return node;
        }

        string nodeExe = Path.Combine(runtimeDir, "node.exe");
        return File.Exists(nodeExe) ? nodeExe : null;
    }

    /// <summary>探测运行时目录是否就位。命中返回 (node 可执行, dsh 入口 bin.js)，否则 null。</summary>
    /// <param name="runtimeDir">运行时目录（捆绑或引导下载同布局）。</param>
    /// <returns>node 与 dsh 入口（位于 node_modules/@deepseek-ai/dsh/lib/bin.js）。</returns>
    /// <remarks>与 pilot-harness 一致：整棵 node_modules 随包收入，入口从 node_modules 解析。</remarks>
    public static (string NodeExe, string DshEntry)? TryLocateBundled(string runtimeDir)
    {
        string? node = TryFindNodeFile(runtimeDir);
        string bin = Path.Combine(runtimeDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        return node is not null && File.Exists(bin) ? (node, bin) : null;
    }

    /// <summary>统一解析出实际就位的运行时目录（捆绑 → 引导下载）；两处都未就位返回 null（只读探测，不创建目录）。
    /// 消费方需要运行时元组时对返回目录再调 <see cref="TryLocateBundled"/>——目录是 <c>AssemblePending</c>
    /// 等种子解析的必要输入，单返回目录可同时服务两个消费面。</summary>
    public static string? TryLocateRuntimeDirectory()
    {
        string runtimeDir = ResolveRuntimeDirectory();
        if (TryLocateBundled(runtimeDir) is not null)
        {
            return runtimeDir;
        }

        // 下载目录未就位时不创建目录（只读探测）
        string downloadDir = ResolveDownloadedRuntimeDirectory();
        return Directory.Exists(downloadDir) && TryLocateBundled(downloadDir) is not null ? downloadDir : null;
    }
}
