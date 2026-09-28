namespace DeepSeek.Harness.Desktop.Infrastructure.Bootstrap;

/// <summary>
/// 随包插件 spawn 前安装（非引导路径；原编排器 <c>InstallCompanionBeforeSpawn</c> 随细搬下沉，ADR
/// post-packaging-churn-restructure 余批）。引导路径的孪生实现见
/// <see cref="FirstBootBootstrapService.InstallBootstrapPluginsAsync"/>（同一
/// <see cref="MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync"/>，日志口径按路径区分）。
/// </summary>
public static class CompanionPreSpawn
{
    /// <summary>安装随包插件（companion）到共享 home。调用时点契约：宿主已建、dsh spawn 之前
    /// （启动主链位置锚定该偏序，对齐参照 dsh-tauri-desk launch.rs：绝不「启动后 3s 装 → 重启」）。</summary>
    /// <param name="bootstrapNeeded">首启引导是否待执行（引导路径自含插件装配，此路径须让位防双装）。</param>
    /// <param name="launch">启动期 A 类配置：dev 显式覆盖共享 home 时跳过（防串扰）。</param>
    public static void EnsureInstalled(bool bootstrapNeeded, LaunchOptions launch)
    {
        // 全局 dsh 模型（ADR simple-shell-single-global-dsh）：dsh 在 PATH 上，nodeExe/dshEntry 传 null，
        // EnsureBundledPluginsBeforeSpawnAsync 内回退到 PATH 上的 dsh 命令。跳过条件：
        // 走首启引导（引导路径装）或 dev 显式覆盖共享 home（防串扰）。
        if (bootstrapNeeded || (launch.IsDev && !launch.DevAutoIsolated))
        {
            return;
        }

        bool installed = false;
        try
        {
            installed = MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                nodeExe: null,
                dshEntry: null,
                HarnessRuntimeHost.ResolveDshHome(),
                Path.Combine(AppContext.BaseDirectory, "resources", "plugins"),
                HostLog.Write,
                PluginProcessRunner.RunAsync,
                PluginProcessRunner.RunProbeAsync,
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // D003 命名所吞：随包插件安装失败不阻断启动（companion 缺席由下次启动自愈）。
            HostLog.Write($"[host] 随包插件 spawn 前安装失败（跳过，不阻断启动）：{ex.Message}");
        }

        if (installed)
        {
            // 事务管线（ADR transactional-plugin-pipeline）：staged 体检在换入前已过，active 即新完整态
            HostLog.Write("[host] 随包插件经事务管线换入 active（staged 体检已过）");
        }
    }
}
