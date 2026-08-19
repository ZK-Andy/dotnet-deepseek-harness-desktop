namespace DeepSeek.Harness.Desktop.Services;

/// <summary>运行时定位：优先探测随应用捆绑的 DSH 运行时（resources/runtime），否则回退 PATH 里的 dsh。</summary>
public static class RuntimeLocator
{
    /// <summary>解析运行时目录：优先环境变量 <c>DSH_DESKTOP_RUNTIME_DIR</c>，否则应用目录下 resources/runtime。</summary>
    public static string ResolveRuntimeDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DSH_DESKTOP_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return Path.Combine(AppContext.BaseDirectory, "resources", "runtime");
    }

    /// <summary>探测捆绑运行时是否就位。命中返回 (node 可执行, dsh 入口 bin.js)，否则 null（回退 PATH dsh）。</summary>
    /// <param name="runtimeDir">运行时目录。</param>
    public static (string NodeExe, string DshEntry)? TryLocateBundled(string runtimeDir)
    {
        var node = Path.Combine(runtimeDir, "node");
        var bin = Path.Combine(runtimeDir, "dsh", "lib", "bin.js");
        return File.Exists(node) && File.Exists(bin) ? (node, bin) : null;
    }
}
