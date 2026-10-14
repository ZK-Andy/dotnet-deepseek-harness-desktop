using DeepSeek.Harness.Desktop.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动用例注册（ADR 组合根机制收官 step-1）：导航/监督用例与三个新端口的适配器在注册处组装，
/// 接线层只解析触发（R1）；时限参数由配置模型 <c>RuntimeTimeouts</c> 单点快照到 Core 侧记录。
/// </summary>
internal static class StartupUseCaseRegistration
{
    /// <summary>登记启动用例与端口适配器（单例；惰性工厂首次解析即创建，与容器既有单例时序一致）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="bootstrap">首启引导服务（监督用例的落定握手句柄来源）。</param>
    /// <param name="proxy">回环代理源；null = 绑定失败（导航用例降级不导航）。</param>
    /// <returns>服务集合（链式）。</returns>
    public static IServiceCollection AddStartupUseCases(
        this IServiceCollection services, IFirstBootBootstrap bootstrap, DshLoopbackProxy? proxy)
    {
        services.AddSingleton<IMintEpoch>(
            sp => new ShellMintEpochAdapter(sp.GetRequiredService<DshShellForward>(), HostLog.Write));
        services.AddSingleton<IWebViewNavigator>(sp => new WebViewNavigator(
            sp.GetRequiredService<CurrentWindowAccessor>(),
            sp.GetRequiredService<RynNavigationCallbacks>()));
        services.AddSingleton<IRecoverySurface>(sp => new RecoverySurface(
            sp.GetRequiredService<HostSetup>().Host,
            sp.GetRequiredService<CurrentWindowAccessor>(),
            sp.GetRequiredService<IFailureDialog>(),
            sp.GetRequiredService<IDiagnosticsExport>(),
            sp.GetRequiredService<UiLocale>(),
            () => sp.GetRequiredService<IRynWindow>(),
            sp.GetRequiredService<TrayController>(),
            HostLog.Write));
        services.AddSingleton(sp => new MainUiEntry(
            sp.GetRequiredService<IWebViewNavigator>(),
            sp.GetRequiredService<DshShellForward>().MintAsync,
            proxy?.Url,
            MainUiOptions(sp.GetRequiredService<RuntimeTimeouts>()),
            WebAuthProbe.Script,
            HostLog.Write));
        services.AddSingleton(sp => new RuntimeSupervision(
            bootstrap,
            sp.GetRequiredService<IRecoverySurface>(),
            sp.GetRequiredService<IMintEpoch>(),
            sp.GetRequiredService<IWebViewNavigator>(),
            proxy?.Url,
            sp.GetRequiredService<UiLocale>(),
            SupervisionOptions(sp.GetRequiredService<RuntimeTimeouts>()),
            HostLog.Write));
        return services;
    }

    /// <summary>进入主界面用例的时限快照（配置模型 → Core 侧记录）。</summary>
    /// <param name="timeouts">运行时超时家（容器单例）。</param>
    /// <returns>用例时限记录。</returns>
    private static MainUiEntryOptions MainUiOptions(RuntimeTimeouts timeouts) => new(
        timeouts.WindowReadyTimeoutSeconds,
        timeouts.WindowReadyPollIntervalSeconds,
        timeouts.AuthProbeTimeoutSeconds,
        timeouts.AuthProbeAttempts,
        timeouts.NavCallTimeoutSeconds,
        timeouts.NavCommitTimeoutSeconds);

    /// <summary>监督用例的时限快照（配置模型 → Core 侧记录）。</summary>
    /// <param name="timeouts">运行时超时家（容器单例）。</param>
    /// <returns>用例时限记录。</returns>
    private static RuntimeSupervisionOptions SupervisionOptions(RuntimeTimeouts timeouts) => new(
        timeouts.SupervisorRestartTimeoutSeconds,
        timeouts.SupervisorRecoveredRetryDelaySeconds,
        timeouts.SupervisorFailedRetryDelaySeconds,
        timeouts.SupervisorBlockedLogEveryRounds);
}
