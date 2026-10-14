namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 主循环承载与退出编排（ADR app-exit-orchestration-core-usecase）：跑 Ryn 主循环、吞主循环异常留痕、
/// 取消监督器与首启引导、按预算 join 监督任务、回收运行时、释放单实例监听器。
/// 编排序即契约——先掐监督面与引导面（避免恢复分支在回收期再 spawn），再回收运行时，最后释放监听器。
/// <para>两条退出路径各自幂等：托盘有序退出/重启经 <see cref="ExitPipeline"/> 的 once-guard 触达回收与
/// 监听器释放；非 orderly 路径（用户直接关窗使主循环返回）不走管道尾部，故监听器释放由调用方经
/// <c>disposeListener</c> 显式传入——两处重复调用均为 no-op（once-guard 与 <see cref="IDisposable.Dispose"/> 幂等）。</para>
/// <para>调用约束：**必须是 Ryn 要求的 thread 0**——<paramref name="mainLoop"/> 经 <c>RynApplication.Run()</c>
/// 阻塞直到窗口关闭，worker/pool 线程调用直接抛（ADR entry-bridge-two-phase）。</para>
/// </summary>
public sealed class AppExitOrchestration
{
    private readonly CancellationTokenSource _supervisorCts;
    private readonly IFirstBootBootstrap _bootstrap;
    private readonly TimeSpan _supervisorJoinBudget;
    private readonly Action<string>? _log;

    /// <summary>创建退出编排用例（依赖经容器解图；join 预算按配置模型投影传入——Core 不引 Infrastructure 的配置模型）。</summary>
    /// <param name="supervisorCts">监督器取消令牌源（容器单例，寿命 = 本次 Run）。</param>
    /// <param name="bootstrap">首启引导端口（取消尚未落定的引导任务）。</param>
    /// <param name="supervisorJoinBudget">监督任务 join 预算（超时即放弃等待，不挡退出）。</param>
    /// <param name="log">日志回调（可选）。</param>
    public AppExitOrchestration(
        CancellationTokenSource supervisorCts,
        IFirstBootBootstrap bootstrap,
        TimeSpan supervisorJoinBudget,
        Action<string>? log = null)
    {
        _supervisorCts = supervisorCts;
        _bootstrap = bootstrap;
        _supervisorJoinBudget = supervisorJoinBudget;
        _log = log;
    }

    /// <summary>跑主循环到窗口关闭，随后按序收口，返回进程退出码（thread 0）。</summary>
    /// <param name="mainLoop">主循环承载（Ryn <c>App.Run</c>：阻塞直到窗口关闭；异常被吞并留痕）。</param>
    /// <param name="supervisorTask">监督任务（join 目标；预算内未结束即放弃等待）。</param>
    /// <param name="reapRuntime">运行时回收动作（退出管道三件套；幂等）。</param>
    /// <param name="disposeListener">释放单实例监听器；null = 未启用（Windows/降级）。</param>
    /// <returns>进程退出码（恒 0）。</returns>
    public int Run(Action mainLoop, Task supervisorTask, Action reapRuntime, Action? disposeListener)
    {
        Log("[host] Ryn Run 开始（阻塞直到窗口关闭）");
        try
        {
            mainLoop();
        }
        catch (Exception ex)
        {
            // 主循环异常不改变退出路径：窗口已不可用，收口照走（留痕供诊断）
            Log($"[host] Ryn Run 异常：{ex}");
        }

        Log("[host] Ryn Run 结束");
        _supervisorCts.Cancel();
        _bootstrap.Cancel();
        try
        {
            supervisorTask.Wait(_supervisorJoinBudget);
        }
        catch (AggregateException)
        {
            // 监督任务随宿主回收而结束；无需上报
        }

        // 非 orderly 退出路径（用户直接关窗使 Run 返回）与托盘有序退出共用同一单实例管道：
        // once-guard 使已回收路径的重复调用为 no-op（退出管道由容器单例供给）
        reapRuntime();
        // 非 orderly 退出路径也要释放单实例锁地址：orderly 路径已 Dispose 过，幂等守卫保证此处安全
        disposeListener?.Invoke();
        return 0;
    }

    /// <summary>日志出口（未注入即静默）。</summary>
    /// <param name="message">一行日志。</param>
    private void Log(string message) => _log?.Invoke(message);
}
