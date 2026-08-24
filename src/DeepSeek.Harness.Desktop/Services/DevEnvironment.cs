namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 开发运行时隔离的纯判定（可单测）。触发条件满足其一：<c>DSH_DESKTOP_RUNTIME_DIR</c> 已设置，
/// 或运行时定位不到捆绑闭包（PATH dsh 回退——<c>dotnet run</code> 调试的典型形态）；
/// 打包安装的产品自带闭包，两者都不命中。
/// 隔离包含两件事：ApplicationId 加 <c>.dev</c> 后缀（避开 GTK 同 id 单实例互斥，使 dev 与
/// 正式版可同时开窗）与 DSH_HOME 默认指向仓库内 <c>.cache/dev-home</c>（杜绝与正式版共享
/// profile 的串扰）。
/// </summary>
public static class DevEnvironment
{
    /// <summary>开发运行时覆盖的环境变量名（RuntimeLocator 同款语义）。</summary>
    public const string RuntimeDirEnv = "DSH_DESKTOP_RUNTIME_DIR";

    /// <summary>DSH_HOME 显式覆盖的环境变量名（单点来源 <see cref="HarnessRuntimeHost.HomeOverrideEnv"/>）。</summary>
    public const string HomeOverrideEnv = HarnessRuntimeHost.HomeOverrideEnv;

    /// <summary>dev 实例的 ApplicationId 后缀（Wayland app_id / GTK unique id 随之变化，任务栏独立条目属预期）。</summary>
    public const string AppIdSuffix = ".dev";

    /// <summary>是否为开发运行时。</summary>
    public static bool IsDevRuntime(string? runtimeDirEnv, bool hasBundledClosure) =>
        !string.IsNullOrWhiteSpace(runtimeDirEnv) || !hasBundledClosure;

    /// <summary>按是否为 dev 返回 ApplicationId（dev 加后缀；非 dev 原样返回）。</summary>
    public static string ApplicationIdFor(string baseId, bool isDev) => isDev ? baseId + AppIdSuffix : baseId;

    /// <summary>
    /// 推导默认隔离 home <c>&lt;仓库根&gt;/.cache/dev-home</c>：
    /// 优先从 runtime 目录两级上溯（<c>&lt;repo&gt;/resources/runtime</c> 形态）；
    /// 否则从应用基目录向上找含 <c>.git</c> 的目录作为仓库根（<c>dotnet run</c> 的 bin 目录形态）。
    /// </summary>
    /// <returns>推导失败返回 null，调用方回退默认 DSH_HOME 并记日志。</returns>
    public static string? DeriveDefaultDevHome(string? runtimeDir, string? baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(runtimeDir))
        {
            var resources = Path.GetDirectoryName(Path.GetFullPath(runtimeDir));
            var root = resources is null ? null : Path.GetDirectoryName(resources);
            if (root is not null)
            {
                return Path.Combine(root, ".cache", "dev-home");
            }
        }

        if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
        {
            return null;
        }

        var current = Path.GetFullPath(baseDirectory);
        while (true)
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return Path.Combine(current, ".cache", "dev-home");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent == current || parent is null)
            {
                return null;
            }

            current = parent;
        }
    }
}
