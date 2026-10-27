namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 首启引导端口（R3：接口在 Core、实现进 Infrastructure、装配面注入）：dsh 进程交互面
/// （全局 node/dsh 引导、插件装配、CLI shim、宿主启动）与引导落定握手句柄；壳侧导航收尾
/// （WebKitGTK 两跳/IPC origin 授权）留装配面，经 <see cref="Start"/> 的就绪回调接回。
/// </summary>
public interface IFirstBootBootstrap
{
    /// <summary>本轮是否需要首启引导（全局 dsh 未检出或落后兼容底线）。</summary>
    bool IsNeeded { get; }

    /// <summary>最近一次 <see cref="Resolve"/> 探测到的全局 dsh 版本（未探测/探测失败为 null）。
    /// 引导未介入时该值即当前运行时版本，启动期告知据此免去第二次 <c>dsh --version</c> spawn。</summary>
    string? ResolvedDshVersion { get; }

    /// <summary>引导重试闸门（引导页 desktop.bootstrap.retry 消费）。</summary>
    RuntimeBootstrapGate Gate { get; }

    /// <summary>解析运行时前提：把系统全局 node bin 暴露到进程 PATH 并探测全局 dsh；
    /// 必须在 spawn 与随包插件装配前调用。</summary>
    void Resolve();

    /// <summary>注册 CLI shim（内容恒定的 pnpm shim；best-effort，不阻断启动）。</summary>
    void RegisterCliShim();

    /// <summary>启动后台引导任务（仅 <see cref="IsNeeded"/> 时启动且只启动一次）；
    /// dsh 就位后回调 <paramref name="onRuntimeReady"/>（壳侧导航收尾）。</summary>
    /// <param name="onRuntimeReady">dsh 就位端点回调（装配面两跳导航/IPC 授权）。</param>
    void Start(Func<DshWebUrl, CancellationToken, Task> onRuntimeReady);

    /// <summary>取消引导任务（应用退出）；未启动时 no-op。</summary>
    void Cancel();

    /// <summary>等待引导落定（成功/失败放弃/取消）。<paramref name="timeout"/> null = 无限等；
    /// 返回 false = 已取消。无首启引导（<see cref="IsNeeded"/> false）立即放行。</summary>
    /// <param name="timeout">降级时限；null = 无限等。</param>
    /// <param name="ct">应用退出取消令牌。</param>
    Task<bool> WaitSettledAsync(TimeSpan? timeout, CancellationToken ct);
}

/// <summary>
/// 首启引导页反馈端口（R3：接口在 Core、实现进 Presentation 页面注入）：引导进度的页面送达单点；
/// 帧形状与重试语义留实现侧。
/// </summary>
public interface IFirstBootUi
{
    /// <summary>推一条引导进度（步骤 + 消息 + 失败标记）。</summary>
    /// <param name="step">引导步骤（与页面 STEP_ORDER 对齐）。</param>
    /// <param name="message">人读步骤消息。</param>
    /// <param name="failed">失败步骤标记。</param>
    Task BootstrapStepAsync(BootstrapStep step, string message, bool failed);
}
