namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 有序退出编排（ADR child-process-reaping-port-drift + self-update-exit-reaps-dsh-child）：
/// 回收三件套（cancel 监督器 → 整树击杀 dsh → 释放 run-marker）先于关窗，看门狗兜底强退。
/// 编排单点化使顺序契约可被记序回归测试钉住——此前内联在 Program.Main，两处「复用」实为复制。
/// </summary>
internal static class ExitOrchestration
{
    /// <summary>回收三件套：cancel 监督器 → host.Stop（整树击杀 dsh）→ RunMarker.Release。
    /// 顺序敏感：先取消监督器（掐断恢复分支再 spawn）再回收子进程；marker 最后释放
    /// （Release 失败仅导致下轮冷启动按「非受控退出」自愈，不能反过来挡住前两步）。</summary>
    public static void ReapRuntime(Action cancelSupervisor, Action stopHost, Action releaseMarker)
    {
        cancelSupervisor();
        stopHost();
        releaseMarker();
    }

    /// <summary>托盘/恢复页有序退出：回收三件套 → 单实例监听器释放 → 关窗 → 看门狗。
    /// 运行时回收必须先于关窗——hide-to-tray 拦截下未批准的 Close 会吞成隐藏，
    /// 回收滞留会把「退出」变成「托盘里的僵尸实例」。</summary>
    public static void OrderlyQuit(Action cancelSupervisor, Action stopHost, Action releaseMarker,
        Action? disposeListener, Action closeWindow, Action startWatchdog)
    {
        ReapRuntime(cancelSupervisor, stopHost, releaseMarker);
        disposeListener?.Invoke();
        closeWindow();
        startWatchdog();
    }
}
