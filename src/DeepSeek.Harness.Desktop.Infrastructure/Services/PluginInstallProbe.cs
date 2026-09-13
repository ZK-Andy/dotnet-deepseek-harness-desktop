using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 插件安装体检探针（ADR plugin-install-health-probe）：独立 spawn 一次 dsh web（<c>--port 0</c>）
/// 验证 <c>dsh web:</c> URL 可出。随事务化插件管线（ADR transactional-plugin-pipeline）的落地，
/// 探针已收敛为 staged 体检——安装驱动在 journal 换入 <em>之前</em> 对 staging 副本探针，
/// 不过即放弃激活（active 保持旧完整态）；原「对 active 事后探针 + reconcile 重试」分支退役。
/// 纯编排与 psi 构造面；spawn 执行在 <see cref="PluginProcessRunner.RunProbeAsync"/>（进程边界单点）。
/// </summary>
public static class PluginInstallProbe
{
    /// <summary>等待探针 URL 的时限：与主 spawn 的 60s 同宽（探针走同一插件树加载路径，慢启动不该被误杀）。
    /// 消费方为 <see cref="PluginProcessRunner.RunProbeAsync"/>。</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>构造探针 spawn 的启动信息：复用 <see cref="HarnessRuntimeHost.BuildStartPsi"/> 单一事实源
    /// （env 净化、PATH 富化、血统 token 同一实现），端口让 OS 分配（<c>--port 0</c>）避开首选端口与
    /// 交接处置；GUI 子系统壳 spawn node 补 CreateNoWindow（对齐 RuntimeBootstrap.BuildCapturePsi）。
    /// <paramref name="homeOverride"/> 供事务管线指 staging home（staged 体检，ADR transactional-plugin-pipeline）。</summary>
    /// <param name="dshHome">共享 DSH_HOME。</param>
    /// <param name="homeOverride">覆盖 DSH_HOME（staging home）；null 时用 <paramref name="dshHome"/>。</param>
    /// <returns>可直接 spawn 的探针启动信息。</returns>
    internal static ProcessStartInfo BuildProbePsi(string dshHome, string? homeOverride = null)
    {
        ProcessStartInfo psi = HarnessRuntimeHost.BuildStartPsi(port: null, homeOverride ?? dshHome, Guid.NewGuid().ToString("N"));
        psi.CreateNoWindow = true;
        return psi;
    }
}
