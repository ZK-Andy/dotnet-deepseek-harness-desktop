namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 桌面 profile 生命周期前置（原组合根/编排器 <c>EnsureDesktopProfile</c> 随细搬下沉，ADR
/// post-packaging-churn-restructure 余批）：migrate → 事务 recover → ensure → reconcile 四步用例编排，
/// 与各步实现（<see cref="DesktopProfileBootstrap"/> / <see cref="PluginProfileTransaction"/>）同层共址。
/// 必须在 spawn 前确保 profile 就绪；失败不阻断启动（dsh 起不来的后果由降级链路兜底）。
/// </summary>
public static class ProfileLifecycle
{
    /// <summary>确保桌面 profile 就绪（幂等，已存在则零写入）。各步语义见方法内注释；任何一步抛出
    /// 均收口为命名日志（<c>catch</c> 命名所吞：profile 初始化失败，dsh 可能拒启）。</summary>
    public static void EnsureReady()
    {
        // 桌面专属 profile 前置（ADR shared-home-desktop-profile / desktop-profile-rename）：
        // 先迁移旧名目录（上游 0.1.5-alpha.1 起 CLI 圈占字面名 desktop），再自举——上游对自定义 profile 名
        // 不自动初始化，缺清单直接拒启；必须在 spawn 前确保 profile 就绪（幂等，已存在则零写入）。
        try
        {
            DesktopProfileBootstrap.MigrateLegacyProfileName(HarnessRuntimeHost.ResolveDshHome(), HostLog.Write);

            // 事务管线 recover（ADR transactional-plugin-pipeline）：上轮插件事务被中断时按 journal
            // 重放/回滚，并清扫 stray staging/rollback 目录。必须在 EnsureProfile/探针/spawn 之前——
            // journal 损坏在此 fail loud（异常进入下方 catch 记日志，dsh 起不来的后果由降级链路兜底）。
            PluginProfileTransaction.Recover(HarnessRuntimeHost.ResolveDshHome(), HostLog.Write);

            if (DesktopProfileBootstrap.EnsureProfile(HarnessRuntimeHost.ResolveDshHome()))
            {
                HostLog.Write($"[host] 已初始化 profiles/{HarnessRuntimeHost.DesktopProfileName}（bundles 对齐 web 模板）");
            }

            // 启动前 reconcile 不可解析的 bundle 引用（ADR online-first-unbundled-runtime 批次三，
            // 对齐 dsh-tauri-desk #177：退役随包种子后，存量 profile 可能残留指向已消失 tgz 的
            // file:/link: 引用，dsh 启动时视作不可解析 → 卡死循环）。必须在 spawn 前清理。
            int reconciled = DesktopProfileBootstrap.ReconcileProfile(HarnessRuntimeHost.ResolveDshHome(), HostLog.Write);
            if (reconciled > 0)
            {
                HostLog.Write($"[host] 桌面 profile reconcile：移除 {reconciled} 个不可解析插件引用");
            }
        }
        catch (Exception ex)
        {
            // D003 命名所吞：profile 初始化失败不阻断启动，dsh 起不来的后果由降级链路兜底。
            HostLog.Write($"[host] profiles/{HarnessRuntimeHost.DesktopProfileName} 初始化失败（dsh 可能拒启，详见后续降级链路）：{ex.Message}");
        }
    }
}
