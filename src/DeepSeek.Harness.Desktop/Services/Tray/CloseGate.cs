namespace DeepSeek.Harness.Desktop.Services.Tray;

/// <summary>
/// 关窗闸门：hide-to-tray 的唯一裁决点。默认拦截关窗（转隐藏到托盘）；
/// <see cref="ApproveExit"/> 后放行——托盘「退出」与自更新安装路径先批准再 Close，
/// 两处共用同一闸门防语义漂移。
/// </summary>
/// <remarks>线程安全：审批来自托盘命令线程，拦截判定发生在 UI 线程 Closing 回调。</remarks>
public sealed class CloseGate
{
    private volatile bool _exitApproved;

    /// <summary>批准本次及后续关窗（幂等）。调用后必须紧接 Close 才有语义。</summary>
    public void ApproveExit() => _exitApproved = true;

    /// <summary>是否应拦截本次关窗（true = 取消并隐藏到托盘）。</summary>
    public bool ShouldCancelClose => !_exitApproved;
}
