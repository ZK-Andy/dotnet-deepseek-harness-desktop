using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 插件安装后启动体检探针（ADR plugin-install-health-probe，对齐官方 healthCheck 的「换血前先证明能活」
/// 廉价子集）：本轮确有插件装成功时、spawn 正式 dsh 前，独立 spawn 一次 dsh web（<c>--port 0</c>）验证
/// <c>dsh web:</c> URL 可出，失败即 ReconcileProfile 后重试一次；二次仍失败记响亮日志后放行（探针是
/// best-effort 增强，绝不阻断启动——失败由既有恢复链路兜底）。纯编排与 psi 构造面；spawn 执行在
/// <see cref="PluginProcessRunner.RunProbeAsync"/>（进程边界单点）。
/// </summary>
public static class PluginInstallProbe
{
    /// <summary>等待探针 URL 的时限：与主 spawn 的 60s 同宽（探针走同一插件树加载路径，慢启动不该被误杀）。
    /// 消费方为 <see cref="PluginProcessRunner.RunProbeAsync"/>。</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 安装后体检：探针一次 → 失败则 reconcile 桌面 profile（清不可解析引用）→ 重试一次。
    /// 返回探针是否验证通过；调用方无论结果都继续正式 spawn（false 只代表「未能提前自愈」）。
    /// </summary>
    /// <param name="dshHome">共享 DSH_HOME。</param>
    /// <param name="log">诊断日志出口（host.log 同款行文）。</param>
    /// <param name="runProbe">执行一次探针 spawn 的注入委托（生产用 <see cref="RunProbeAsync"/>，测试用 fake）。</param>
    /// <param name="reconcile">reconcile 注入委托（生产用 <see cref="DesktopProfileBootstrap.ReconcileProfile"/>，
    /// 返回移除的引用数——调用序断言与「有改动才重试」的日志分支都依赖它）。</param>
    /// <param name="ct">取消令牌（应用退出时探针随链路取消，OCE 上抛由调用方收口）。</param>
    /// <returns>探针验证通过返回 true；两次都失败返回 false。</returns>
    public static async Task<bool> VerifyAfterInstallAsync(
        string dshHome,
        Action<string> log,
        Func<ProcessStartInfo, CancellationToken, Task<Uri?>> runProbe,
        Func<string, Action<string>, int> reconcile,
        CancellationToken ct)
    {
        log("[host] 插件体检探针：本轮有插件安装，先验证 dsh web 可出 URL 再正式启动");
        if (await runProbe(BuildProbePsi(dshHome), ct).ConfigureAwait(false) is not null)
        {
            log("[host] 插件体检探针通过");
            return true;
        }

        log("[host] 插件体检探针未出 URL：reconcile 桌面 profile 后重试一次");
        int removed = reconcile(dshHome, log);
        if (removed > 0)
        {
            log($"[host] 插件体检探针：reconcile 移除 {removed} 个不可解析引用，重试探针");
        }

        if (await runProbe(BuildProbePsi(dshHome), ct).ConfigureAwait(false) is not null)
        {
            log("[host] 插件体检探针：reconcile 后通过");
            return true;
        }

        log("[host] 插件体检探针二次未出 URL（reconcile 后仍不可启动）：放行正式启动，失败由恢复链路兜底");
        return false;
    }

    /// <summary>构造探针 spawn 的启动信息：复用 <see cref="HarnessRuntimeHost.BuildStartPsi"/> 单一事实源
    /// （env 净化、PATH 富化、血统 token 同一实现），端口让 OS 分配（<c>--port 0</c>）避开首选端口与
    /// 交接处置；GUI 子系统壳 spawn node 补 CreateNoWindow（对齐 RuntimeBootstrap.BuildCapturePsi）。</summary>
    /// <param name="dshHome">共享 DSH_HOME。</param>
    /// <returns>可直接 spawn 的探针启动信息。</returns>
    internal static ProcessStartInfo BuildProbePsi(string dshHome)
    {
        ProcessStartInfo psi = HarnessRuntimeHost.BuildStartPsi(port: null, dshHome, Guid.NewGuid().ToString("N"));
        psi.CreateNoWindow = true;
        return psi;
    }
}
