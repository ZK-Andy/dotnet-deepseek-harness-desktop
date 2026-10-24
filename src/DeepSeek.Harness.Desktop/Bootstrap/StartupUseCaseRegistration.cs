using DeepSeek.Harness.Desktop.Core.Ui;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动用例的时限映射（ADR 组合根机制收官 step-1）：配置模型 <c>RuntimeTimeouts</c> 在唯一处快照成
/// Core 侧记录，壳装配只取用记录。映射与配置键一一对应，两处同值键不允许各抄一份。
/// </summary>
internal static class StartupUseCaseRegistration
{
    /// <summary>进入主界面用例的时限快照（配置模型 → Core 侧记录）。</summary>
    /// <param name="timeouts">运行时超时家（装配期共享态）。</param>
    /// <returns>用例时限记录。</returns>
    internal static MainUiEntryOptions MainUiOptions(RuntimeTimeouts timeouts) => new(
        timeouts.WindowReadyTimeoutSeconds,
        timeouts.WindowReadyPollIntervalSeconds,
        timeouts.AuthProbeTimeoutSeconds,
        timeouts.AuthProbeAttempts,
        timeouts.NavCallTimeoutSeconds,
        timeouts.NavCommitTimeoutSeconds);

    /// <summary>监督用例的时限快照（配置模型 → Core 侧记录）。</summary>
    /// <param name="timeouts">运行时超时家（装配期共享态）。</param>
    /// <returns>用例时限记录。</returns>
    internal static RuntimeSupervisionOptions SupervisionOptions(RuntimeTimeouts timeouts) => new(
        timeouts.SupervisorRestartTimeoutSeconds,
        timeouts.SupervisorRecoveredRetryDelaySeconds,
        timeouts.SupervisorFailedRetryDelaySeconds,
        timeouts.SupervisorBlockedLogEveryRounds,
        timeouts.SupervisorFailureSurfaceStuckThreshold);
}
