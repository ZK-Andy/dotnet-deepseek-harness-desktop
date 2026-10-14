namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 壳铸币态端口适配器（R3：端口在 Core、实现是 Infrastructure 的 <c>DshShellForward</c>，接线在组合点；
/// ADR 组合根机制收官 step-1 的 B2）：失效与收养重验逐句转发，不夹带决策。
/// </summary>
internal sealed class ShellMintEpochAdapter : IMintEpoch
{
    private readonly DshShellForward _forward;
    private readonly Action<string> _log;

    /// <summary>创建适配器。</summary>
    /// <param name="forward">壳转发器（铸币态唯一家）。</param>
    /// <param name="log">日志出口（转发器只记状态/头名，值永不落盘）。</param>
    public ShellMintEpochAdapter(DshShellForward forward, Action<string> log)
    {
        _forward = forward;
        _log = log;
    }

    /// <inheritdoc />
    public bool Invalidate() => _forward.InvalidateRoute();

    /// <inheritdoc />
    public async Task RevalidateAsync(DshWebUrl url, CancellationToken ct) =>
        _ = await _forward.RevalidateAsync(url, _log, ct).ConfigureAwait(false);
}
