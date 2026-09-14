namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// 单实例退出管道（ADR composition-root-value-flow-pipeline）：有序退出步骤是构造数据，幂等由
/// 内嵌 once-guard 保证——托盘有序退出、Run 尾部回收、自更新兜底强退共用同一实例。
/// 回收序（cancel 监督器 → 停宿主 → 释放 marker）先于关窗：hide-to-tray 拦截下未批准的 Close
/// 会吞成隐藏，回收滞留会把「退出」变成托盘里的僵尸实例。
/// </summary>
internal sealed class ExitPipeline
{
    private readonly Action _cancelSupervisor;
    private readonly Action _stopHost;
    private readonly Action _releaseMarker;
    private readonly Action? _disposeListener;
    private readonly Action _closeWindow;
    private readonly Action _startWatchdog;
    private readonly Action<string>? _log;
    private int _reapDone;
    private int _orderlyQuitDone;

    /// <summary>创建退出管道（有序步骤即构造数据）。</summary>
    /// <param name="cancelSupervisor">取消监督器（掐断恢复分支再 spawn）。</param>
    /// <param name="stopHost">停止运行时宿主（整树击杀 dsh）。</param>
    /// <param name="releaseMarker">释放 run-marker（失败仅致下轮按非受控退出自愈，不挡前两步）。</param>
    /// <param name="disposeListener">释放单实例监听器；null = 未启用（Windows/降级）。</param>
    /// <param name="closeWindow">关闭窗口。</param>
    /// <param name="startWatchdog">看门狗启动动作；null = 真看门狗（<see cref="ScheduleQuitWatchdog"/>），测试可注入记序 fake。</param>
    /// <param name="log">日志回调（可选）。</param>
    public ExitPipeline(
        Action cancelSupervisor,
        Action stopHost,
        Action releaseMarker,
        Action? disposeListener,
        Action closeWindow,
        Action? startWatchdog = null,
        Action<string>? log = null)
    {
        _cancelSupervisor = cancelSupervisor;
        _stopHost = stopHost;
        _releaseMarker = releaseMarker;
        _disposeListener = disposeListener;
        _closeWindow = closeWindow;
        _startWatchdog = startWatchdog ?? ScheduleQuitWatchdog;
        _log = log;
    }

    /// <summary>回收三件套（cancel 监督器 → 停宿主 → 释放 marker）；once-guard：重复调用为 no-op。
    /// 有序步骤全部为构造数据，幂等由单点构造保证（ADR composition-root-value-flow-pipeline）。</summary>
    public void ReapRuntime()
    {
        if (Interlocked.Exchange(ref _reapDone, 1) != 0)
        {
            return;
        }

        _cancelSupervisor();
        _stopHost();
        _releaseMarker();
    }

    /// <summary>托盘/恢复页有序退出：回收三件套 → 释放监听器 → 关窗 → 看门狗；
    /// once-guard：重复调用为 no-op。</summary>
    public void OrderlyQuit()
    {
        if (Interlocked.Exchange(ref _orderlyQuitDone, 1) != 0)
        {
            return;
        }

        _log?.Invoke("[tray] 有序退出：回收运行时后关闭窗口");
        ReapRuntime();
        _disposeListener?.Invoke();
        _closeWindow();
        _startWatchdog();
    }

    /// <summary>8s 退出看门狗（无令牌，退出即终态）：主循环届时仍未返回则先补一次 Stop（幂等）再强制终结。
    /// Exit 不展开栈，已显式完成的 Cancel/Stop/Release/unlink 不会被二次执行，无双重释放面。</summary>
    public void ScheduleQuitWatchdog()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            _log?.Invoke("[tray] 退出看门狗触发：主循环未返回，强制结束");
            _stopHost();
            Environment.Exit(0);
        });
    }

    /// <summary>安装授权通过后的兜底退出：窗口 Close 未生效时回收运行时后强制结束进程，放行安装脚本。
    /// 裸 <c>Environment.Exit(0)</c> 会绕过 Run 尾部的 <c>host.Stop()</c>，dsh 子进程成孤儿占住首选端口
    /// （ADR self-update-exit-reaps-dsh-child）。</summary>
    /// <param name="ct">取消令牌：授权窗口内取消则放弃兜底。</param>
    public void ScheduleExitFallback(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _log?.Invoke("[update] 退出兜底触发：回收 dsh 后强退");
            _log?.Invoke("[update] 兜底回收：cancel 监督器 + 整树击杀 dsh + 释放 marker");
            ReapRuntime();
            Environment.Exit(0);
        });
    }
}
