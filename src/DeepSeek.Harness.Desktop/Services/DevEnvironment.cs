namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 开发运行时隔离的纯判定（可单测）。触发标记 = <c>DSH_DESKTOP_RUNTIME_DIR</c> 已设置
/// （打包产品永不设置该变量）；隔离包含两件事：ApplicationId 加 <c>.dev</c> 后缀
/// （避开 GTK 同 id 单实例互斥，使 dev 与正式版可同时开窗）与 DSH_HOME 默认指向仓库内
/// <c>.cache/dev-home</c>（杜绝与正式版共享 profile 的串扰）。
/// </summary>
public static class DevEnvironment
{
    /// <summary>开发运行时覆盖的环境变量名（RuntimeLocator 同款语义）。</summary>
    public const string RuntimeDirEnv = "DSH_DESKTOP_RUNTIME_DIR";

    /// <summary>DSH_HOME 显式覆盖的环境变量名（HarnessRuntimeHost 同款语义）。</summary>
    public const string HomeOverrideEnv = "DSH_DESKTOP_DSH_HOME";

    /// <summary>dev 实例的 ApplicationId 后缀（Wayland app_id / GTK unique id 随之变化，任务栏独立条目属预期）。</summary>
    public const string AppIdSuffix = ".dev";

    /// <summary>是否为开发运行时。</summary>
    public static bool IsDevRuntime(string? runtimeDirEnv) => !string.IsNullOrWhiteSpace(runtimeDirEnv);

    /// <summary>按是否为 dev 返回 ApplicationId（dev 加后缀；非 dev 原样返回）。</summary>
    public static string ApplicationIdFor(string baseId, bool isDev) => isDev ? baseId + AppIdSuffix : baseId;

    /// <summary>
    /// 从 runtime 目录推导默认隔离 home：<c>&lt;runtimeDir&gt;/../../.cache/dev-home</c>
    /// （runtimeDir 形如 <c>&lt;repo&gt;/resources/runtime</c>）。
    /// </summary>
    /// <returns>推导失败（目录结构不符）返回 null，调用方回退默认 DSH_HOME 并记日志。</returns>
    public static string? DeriveDefaultDevHome(string? runtimeDir)
    {
        if (string.IsNullOrWhiteSpace(runtimeDir))
        {
            return null;
        }

        var resources = Path.GetDirectoryName(Path.GetFullPath(runtimeDir));
        var root = resources is null ? null : Path.GetDirectoryName(resources);
        return root is null ? null : Path.Combine(root, ".cache", "dev-home");
    }
}
