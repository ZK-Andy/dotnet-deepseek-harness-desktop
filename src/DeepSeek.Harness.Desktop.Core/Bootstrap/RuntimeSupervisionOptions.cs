namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 运行时监督用例的时限与留痕参数（Core 侧投影；可调参数的唯一家在 <c>RuntimeTimeouts</c>，
/// 由组合点按配置模型快照构造，本记录不持默认值——避免同一事实出现第二个家）。
/// </summary>
/// <param name="RestartTimeoutSeconds">单次重启等待 URL 的时限。</param>
/// <param name="RecoveredRetryDelaySeconds">重启未给出 URL 后的重试延迟。</param>
/// <param name="FailedRetryDelaySeconds">恢复失败后的重试延迟。</param>
/// <param name="BlockedLogEveryRounds">残留锁死同因重试的留痕步长（首轮必留痕）。</param>
/// <param name="FailureSurfaceStuckThreshold">失败面卡住阈值（连续未恢复崩溃数；达到才弹面，恢复成功复位；≤1 回到每次都弹）。</param>
public sealed record RuntimeSupervisionOptions(
    int RestartTimeoutSeconds,
    int RecoveredRetryDelaySeconds,
    int FailedRetryDelaySeconds,
    int BlockedLogEveryRounds,
    int FailureSurfaceStuckThreshold);
