namespace DeepSeek.Harness.Desktop.Core.Ui;

/// <summary>
/// 进入主界面用例的时限与重试参数（Core 侧投影；可调参数的唯一家在 <c>RuntimeTimeouts</c>，
/// 由组合点按配置模型快照构造，本记录不持默认值——避免同一事实出现第二个家）。
/// </summary>
/// <param name="WindowReadyTimeoutSeconds">等主窗口可用的总时限（超时 loud 跳过本次导航）。</param>
/// <param name="WindowReadyPollIntervalSeconds">等主窗口可用的轮询节拍。</param>
/// <param name="AuthProbeTimeoutSeconds">单次终页探针的时限。</param>
/// <param name="AuthProbeAttempts">终页探针的尝试次数上限（耗尽按未知放过启动）。</param>
/// <param name="NavCallTimeoutSeconds">单次导航调用的时限（超时按「已提交」继续）。</param>
/// <param name="NavCommitTimeoutSeconds">导航提交等待的时限（超时按「已提交」继续）。</param>
public sealed record MainUiEntryOptions(
    int WindowReadyTimeoutSeconds,
    int WindowReadyPollIntervalSeconds,
    int AuthProbeTimeoutSeconds,
    int AuthProbeAttempts,
    int NavCallTimeoutSeconds,
    int NavCommitTimeoutSeconds);
