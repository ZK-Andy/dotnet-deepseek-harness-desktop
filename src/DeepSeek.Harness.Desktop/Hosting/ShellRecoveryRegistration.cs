using Avalonia.Controls;
using DeepSeek.Harness.Desktop.Core.Ui;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// 恢复/引导/监督三域的装配（ADR implemented/architecture/2026-10-23-avalonia-g1g3-recovery-bootstrap-supervision）：
/// 把 Core 的 <see cref="MainUiEntry"/>（引导完成后的铸币与终页裁决）与
/// <see cref="RuntimeSupervision"/>（崩溃恢复面、重试与收口导航）、Presentation 的
/// <see cref="RecoverySurface"/>（原生优先、页内回落）和两条页面路由接进新宿主。
/// 基础设施具体类型的 <c>new</c> 收在本注册文件（R1a 注册纪律）；装配面只调用并接线。
/// </summary>
internal static class ShellRecoveryRegistration
{
    /// <summary>构造恢复面、监督用例、主界面用例与两条路由。</summary>
    /// <param name="host">运行时宿主（失败面正文的 stderr 尾源、监督循环的监视对象）。</param>
    /// <param name="surface">已挂载的网页视图可测面（导航、到达信号与脚本求值）。</param>
    /// <param name="scriptTarget">页面脚本求值端口（恢复页覆写 DOM 用）。</param>
    /// <param name="proxyUrl">回环代理源根；null = 绑定失败（收口导航退化为不导航）。</param>
    /// <param name="bootstrap">首启引导服务（监督的落定门控与引导重试闸门）。</param>
    /// <param name="diagnostics">诊断导出端口（与 desktop.diagnostics.export 共用同一导出器）。</param>
    /// <param name="uiLocale">宿主 UI 语言单点。</param>
    /// <param name="closeGate">关窗闸门（恢复页退出与托盘退出同一条批准契约）。</param>
    /// <param name="owner">失败面模态的属主窗口取法（调用期满；窗口可能已关闭）。</param>
    /// <param name="closeWindow">关窗动作（UI 线程封送由调用方负责）。</param>
    /// <param name="timeouts">运行时超时家（用例时限映射）。</param>
    /// <param name="shellForward">壳转发器（铸币态失效/重验）。</param>
    /// <param name="log">日志出口。</param>
    /// <returns>接线产物（主界面用例、监督用例与两条路由）。</returns>
    public static ShellRecoveryAssembly Create(
        HarnessRuntimeHost host,
        IWebViewSurface surface,
        IWindowScriptTarget scriptTarget,
        Uri? proxyUrl,
        IFirstBootBootstrap bootstrap,
        IDiagnosticsExport diagnostics,
        UiLocale uiLocale,
        CloseGate closeGate,
        Func<Window?> owner,
        Action closeWindow,
        RuntimeTimeouts timeouts,
        DshShellForward shellForward,
        Action<string> log)
    {
        var navigator = new AvaloniaWebViewNavigator(surface);
        var dialog = new InProcessFailureDialog(owner, uiLocale, log);
        var recovery = new RecoverySurface(
            host,
            scriptTarget,
            dialog,
            diagnostics,
            uiLocale,
            closeGate,
            closeWindow,
            log);
        var supervision = new RuntimeSupervision(
            bootstrap,
            recovery,
            new ShellMintEpochAdapter(shellForward, log),
            navigator,
            proxyUrl,
            uiLocale,
            Bootstrap.StartupUseCaseRegistration.SupervisionOptions(timeouts),
            log);
        var entry = new MainUiEntry(
            navigator,
            shellForward.MintAsync,
            proxyUrl,
            Bootstrap.StartupUseCaseRegistration.MainUiOptions(timeouts),
            WebAuthProbe.Script,
            log);
        ICommandRouter[] routers =
        [
            new BootstrapCommandRouter(bootstrap.Gate, log),
            new RecoveryCommandRouter(closeWindow, closeGate, log),
        ];
        return new ShellRecoveryAssembly(entry, supervision, routers);
    }
}

/// <summary>恢复/引导/监督装配的接线产物（装配面按需取用，不暴露中间件）。</summary>
/// <param name="Entry">主界面用例（引导完成后的铸币与终页裁决）。</param>
/// <param name="Supervision">监督用例（引导落定后进入恢复循环）。</param>
/// <param name="Routers">页面路由（引导重试与恢复页退出）。</param>
internal sealed record ShellRecoveryAssembly(
    MainUiEntry Entry,
    RuntimeSupervision Supervision,
    IReadOnlyList<ICommandRouter> Routers);

/// <summary>恢复/引导/监督的宿主侧依赖束（装配面一次收参，避免长参数表再散成字段）。</summary>
/// <param name="Bootstrap">首启引导服务（引导启动、重试闸门与监督落定门控）。</param>
/// <param name="Host">运行时宿主（失败面尾源与监督对象）。</param>
/// <param name="SupervisorCts">监督器取消令牌源（寿命 = 本次 Run；退出管道取消）。</param>
/// <param name="ShellForward">壳转发器（铸币态失效/重验）。</param>
/// <param name="Timeouts">运行时超时家（用例时限映射）。</param>
internal sealed record ShellRecoveryContext(
    IFirstBootBootstrap Bootstrap,
    HarnessRuntimeHost Host,
    CancellationTokenSource SupervisorCts,
    DshShellForward ShellForward,
    RuntimeTimeouts Timeouts,
    Action<string> Log)
{
    /// <summary>监督循环任务（引导落定后起，退出路径 join 用）。</summary>
    public Task? SupervisionTask { get; set; }

    /// <summary>取消引导与监督（不等待）：窗口关闭路径用——它在 UI 线程上跑，而监督循环的
    /// 页内恢复面求值要同一条 UI 线程，阻塞等待会自锁。</summary>
    public void Cancel()
    {
        SupervisorCts.Cancel();
        Bootstrap.Cancel();
    }

    /// <summary>取消并按时限 join 监督任务（只在池线程的退出管道调用）：避免「cancel 后立即 Stop」
    /// 撞上在飞重启——<c>HarnessRuntimeHost.Stop</c> 的前提是监督器已停。join 超时留痕后放行，
    /// 进程看门狗兜底。</summary>
    public void CancelAndJoin()
    {
        Cancel();
        Task? task = SupervisionTask;
        if (task is null)
        {
            return;
        }

        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(Timeouts.SupervisorJoinTimeoutSeconds)))
            {
                Log("[host] 退出时监督循环未在时限内收口：继续回收，残留交看门狗/冷启动清扫");
            }
        }
        catch (AggregateException)
        {
            // 监督循环的异常已由接线段留痕（OnlyOnFaulted 续体）；此处只负责收口。
        }
    }
}
