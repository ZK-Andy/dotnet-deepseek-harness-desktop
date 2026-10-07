using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// 运行时超时/重试的可调参数（appsettings.json 的 <c>RuntimeTimeouts</c> 节，禁止硬编码进逻辑）。
/// 全部默认值 = 收归前的字面量（同值迁移零行为变更，见 ADR timeout-hardcode-to-appsettings）：
/// 协议/安全不变量（回环地址、端口范围、sha 长度、退出码、看门狗 bound 等）不在此列，保持固定。
/// </summary>
public sealed record RuntimeTimeouts
{
    /// <summary>relay 等待的探测节拍（秒）。</summary>
    public int RelayWaitIntervalSeconds { get; init; } = 1;

    /// <summary>市场 helper 宽限窗（秒）：崩溃恢复路径的额外等待不超过此窗。</summary>
    public int RelayHelperGraceSeconds { get; init; } = 2;

    /// <summary>接力就绪的稳定窗（秒）：就绪须连续维持该窗才收养。</summary>
    public int RelayReadyStableWindowSeconds { get; init; } = 2;

    /// <summary>收养进程退出轮询节拍（秒）。</summary>
    public int AdoptedExitPollIntervalSeconds { get; init; } = 1;

    /// <summary>端口探活的单次超时（毫秒）。</summary>
    public int PortProbeTimeoutMilliseconds { get; init; } = 500;

    /// <summary>web 面就绪探针的单次超时（毫秒）。</summary>
    public int WebProbeTimeoutMilliseconds { get; init; } = 1500;

    /// <summary>Stop 生命周期门的等待上限（秒）：超时跳过回收，残留交冷启动孤儿清扫。</summary>
    public int LifecycleGateWaitSeconds { get; init; } = 3;

    /// <summary>单次 spawn 等待 URL 的时限（秒）；插件探针走同一插件树加载路径，与主 spawn 同宽，
    /// 共用此源（耦合不变量合并，不另设独立键，见 <c>PluginInstallProbe.ProbeTimeout</c>）。</summary>
    public int SpawnTimeoutSeconds { get; init; } = 60;

    /// <summary>二启通知主实例的超时（秒）。</summary>
    public int NotifyPrimaryTimeoutSeconds { get; init; } = 2;

    /// <summary>退出时等监督任务收尾的上限（秒）。</summary>
    public int SupervisorJoinTimeoutSeconds { get; init; } = 2;

    /// <summary>单次崩溃重启等待 URL 的时限（秒，经 <c>RuntimeSupervisor</c> 构造注入）。</summary>
    public int SupervisorRestartTimeoutSeconds { get; init; } = 60;

    /// <summary>重启未给出 URL 后的重试延迟（秒）。</summary>
    public int SupervisorRecoveredRetryDelaySeconds { get; init; } = 2;

    /// <summary>恢复失败后的重试延迟（秒）。</summary>
    public int SupervisorFailedRetryDelaySeconds { get; init; } = 1;

    /// <summary>残留锁死同因重试的日志留痕步长（轮）：首轮必留痕，此后每隔该轮数留痕一次；
    /// 重探本身仍按失败延迟每轮执行。默认 60（失败延迟 1s 时 ≈ 每分钟一条）。</summary>
    public int SupervisorBlockedLogEveryRounds { get; init; } = 60;

    /// <summary>页面健康监视首拍延迟（秒）：避开启动空窗。</summary>
    public int HealthInitialDelaySeconds { get; init; } = 10;

    /// <summary>引导落定等待的降级时限（秒）：超时按已定继续。</summary>
    public int BootstrapSettleTimeoutSeconds { get; init; } = 120;

    /// <summary>导航提交信号等待超时（秒）：超时按已提交继续。</summary>
    public int NavCommitTimeoutSeconds { get; init; } = 5;

    /// <summary>导航调用本身超时（秒）：原生调用可挂起（arm64 实证），无界等即永卡；
    /// 超时 loud 后按已提交继续（ADR navigate-call-timeout；默认 30s）。</summary>
    public int NavCallTimeoutSeconds { get; init; } = 30;

    /// <summary>鉴权自愈的可见文本探针超时（秒）：超时按未知跳过自愈，绝不拖死启动（病 renderer 超时 30s 实证）。</summary>
    public int AuthProbeTimeoutSeconds { get; init; } = 15;

    /// <summary>鉴权探针总尝试次数（含初次）：偶发 renderer 繁忙一次采样赌运气，耗尽回未知放行
    /// （ADR page-verdict-gate，自 settle-gate-and-probe-retry 并入；默认 2 即初次 + 1 次重试；≤1 按单次，与既有键同哲学无钳制）。</summary>
    public int AuthProbeAttempts { get; init; } = 2;

    /// <summary>进入主界面前等主窗口可用超时（秒）：原生建窗可能慢于 dsh 就位（CI 无 D-Bus 会话实证 30s+），
    /// 超时跳过本次导航并 loud 留痕（ADR bootstrap-window-ready-wait）。</summary>
    public int WindowReadyTimeoutSeconds { get; init; } = 120;

    /// <summary>等主窗口可用的轮询节拍（秒）：采样粒度非决策阈值，默认 1s（同类节拍键先例）。</summary>
    public int WindowReadyPollIntervalSeconds { get; init; } = 1;

    /// <summary>单实例 IPC 服务端单次读超时（秒）。</summary>
    public int IpcServeTimeoutSeconds { get; init; } = 5;

    /// <summary>单实例 accept 循环重试延迟（秒）。</summary>
    public int IpcAcceptRetryDelaySeconds { get; init; } = 1;

    /// <summary>版本探测超时（秒）。版本底线本身是协议不变量，不在此列。</summary>
    public int VersionProbeTimeoutSeconds { get; init; } = 8;

    /// <summary>横幅注入重试上限（次）。</summary>
    public int BannerMaxAttempts { get; init; } = 30;

    /// <summary>横幅注入重试节拍（秒）。</summary>
    public int BannerRetryDelaySeconds { get; init; } = 1;

    /// <summary>进度/状态推送重试上限（次）。</summary>
    public int PushMaxAttempts { get; init; } = 15;

    /// <summary>进度/状态推送重试节拍（毫秒）。</summary>
    public int PushRetryDelayMilliseconds { get; init; } = 400;

    /// <summary>顶栏脚本注册的到点预算（秒，节拍沿用 <see cref="PushRetryDelayMilliseconds"/>，按
    /// <c>ceil(本值 / 节拍)</c> 折算成尝试次数）：按次数计的 6s 预算不够 mac x64（Rosetta）下的 webview
    /// 创建（发版腿实证重试耗尽后 2s 才导航到达），故改到点（ADR frameless-uniform-caption-bar）。
    /// 0 = 停用该注册路（不尝试）；节拍非正值时预算不可度量，按单次尝试处理。</summary>
    public int CaptionBarRegisterSeconds { get; init; } = 60;

    /// <summary>从应用旁的 appsettings.json 读取 <c>RuntimeTimeouts</c> 节；文件缺失或节缺失时全默认；
    /// 文件不可读/损坏/取值不可用也回退全默认并留痕，不阻塞启动。</summary>
    /// <param name="baseDirectory">appsettings.json 所在目录。</param>
    /// <param name="log">失败留痕出口（可空；启动路径传 <c>HostLog.Write</c>）。</param>
    public static RuntimeTimeouts Load(string baseDirectory, Action<string>? log = null) =>
        ConfigSectionFile.LoadFile(baseDirectory, nameof(RuntimeTimeouts), Parse, new RuntimeTimeouts(), log);

    /// <summary>把 appsettings.json 全文解析为 <see cref="RuntimeTimeouts"/>（纯函数，可单测）：
    /// 根非对象、无 <c>RuntimeTimeouts</c> 节、节非对象、键缺失或取值不可表示（小数/越界）
    /// 一律回退默认；损坏 JSON 由调用方转 fail-safe（本方法除坏 JSON 外不抛）。</summary>
    /// <param name="json">appsettings.json 全文。</param>
    /// <returns>解析后的选项（缺省字段保持默认值）。</returns>
    internal static RuntimeTimeouts Parse(string json) =>
        ConfigSectionJson.Parse(json, "RuntimeTimeouts", ParseSection, new RuntimeTimeouts());

    private static RuntimeTimeouts ParseSection(JsonElement section)
    {
        var options = new RuntimeTimeouts();
        options = options with { RelayWaitIntervalSeconds = ConfigSectionJson.GetInt(section, nameof(RelayWaitIntervalSeconds), options.RelayWaitIntervalSeconds) };
        options = options with { RelayHelperGraceSeconds = ConfigSectionJson.GetInt(section, nameof(RelayHelperGraceSeconds), options.RelayHelperGraceSeconds) };
        options = options with { RelayReadyStableWindowSeconds = ConfigSectionJson.GetInt(section, nameof(RelayReadyStableWindowSeconds), options.RelayReadyStableWindowSeconds) };
        options = options with { AdoptedExitPollIntervalSeconds = ConfigSectionJson.GetInt(section, nameof(AdoptedExitPollIntervalSeconds), options.AdoptedExitPollIntervalSeconds) };
        options = options with { PortProbeTimeoutMilliseconds = ConfigSectionJson.GetInt(section, nameof(PortProbeTimeoutMilliseconds), options.PortProbeTimeoutMilliseconds) };
        options = options with { WebProbeTimeoutMilliseconds = ConfigSectionJson.GetInt(section, nameof(WebProbeTimeoutMilliseconds), options.WebProbeTimeoutMilliseconds) };
        options = options with { LifecycleGateWaitSeconds = ConfigSectionJson.GetInt(section, nameof(LifecycleGateWaitSeconds), options.LifecycleGateWaitSeconds) };
        options = options with { SpawnTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(SpawnTimeoutSeconds), options.SpawnTimeoutSeconds) };
        options = options with { NotifyPrimaryTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(NotifyPrimaryTimeoutSeconds), options.NotifyPrimaryTimeoutSeconds) };
        options = options with { SupervisorJoinTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(SupervisorJoinTimeoutSeconds), options.SupervisorJoinTimeoutSeconds) };
        options = options with { SupervisorRestartTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(SupervisorRestartTimeoutSeconds), options.SupervisorRestartTimeoutSeconds) };
        options = options with { SupervisorRecoveredRetryDelaySeconds = ConfigSectionJson.GetInt(section, nameof(SupervisorRecoveredRetryDelaySeconds), options.SupervisorRecoveredRetryDelaySeconds) };
        options = options with { SupervisorFailedRetryDelaySeconds = ConfigSectionJson.GetInt(section, nameof(SupervisorFailedRetryDelaySeconds), options.SupervisorFailedRetryDelaySeconds) };
        options = options with { SupervisorBlockedLogEveryRounds = ConfigSectionJson.GetInt(section, nameof(SupervisorBlockedLogEveryRounds), options.SupervisorBlockedLogEveryRounds) };
        options = options with { HealthInitialDelaySeconds = ConfigSectionJson.GetInt(section, nameof(HealthInitialDelaySeconds), options.HealthInitialDelaySeconds) };
        options = options with { BootstrapSettleTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(BootstrapSettleTimeoutSeconds), options.BootstrapSettleTimeoutSeconds) };
        options = options with { NavCommitTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(NavCommitTimeoutSeconds), options.NavCommitTimeoutSeconds) };
        options = options with { NavCallTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(NavCallTimeoutSeconds), options.NavCallTimeoutSeconds) };
        options = options with { AuthProbeTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(AuthProbeTimeoutSeconds), options.AuthProbeTimeoutSeconds) };
        options = options with { AuthProbeAttempts = ConfigSectionJson.GetInt(section, nameof(AuthProbeAttempts), options.AuthProbeAttempts) };
        options = options with { WindowReadyTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(WindowReadyTimeoutSeconds), options.WindowReadyTimeoutSeconds) };
        options = options with { WindowReadyPollIntervalSeconds = ConfigSectionJson.GetInt(section, nameof(WindowReadyPollIntervalSeconds), options.WindowReadyPollIntervalSeconds) };
        options = options with { IpcServeTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(IpcServeTimeoutSeconds), options.IpcServeTimeoutSeconds) };
        options = options with { IpcAcceptRetryDelaySeconds = ConfigSectionJson.GetInt(section, nameof(IpcAcceptRetryDelaySeconds), options.IpcAcceptRetryDelaySeconds) };
        options = options with { VersionProbeTimeoutSeconds = ConfigSectionJson.GetInt(section, nameof(VersionProbeTimeoutSeconds), options.VersionProbeTimeoutSeconds) };
        options = options with { BannerMaxAttempts = ConfigSectionJson.GetInt(section, nameof(BannerMaxAttempts), options.BannerMaxAttempts) };
        options = options with { BannerRetryDelaySeconds = ConfigSectionJson.GetInt(section, nameof(BannerRetryDelaySeconds), options.BannerRetryDelaySeconds) };
        options = options with { PushMaxAttempts = ConfigSectionJson.GetInt(section, nameof(PushMaxAttempts), options.PushMaxAttempts) };
        options = options with { PushRetryDelayMilliseconds = ConfigSectionJson.GetInt(section, nameof(PushRetryDelayMilliseconds), options.PushRetryDelayMilliseconds) };
        options = options with { CaptionBarRegisterSeconds = ConfigSectionJson.GetInt(section, nameof(CaptionBarRegisterSeconds), options.CaptionBarRegisterSeconds) };
        return options;
    }
}
