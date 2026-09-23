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

    /// <summary>页面健康监视首拍延迟（秒）：避开启动空窗。</summary>
    public int HealthInitialDelaySeconds { get; init; } = 10;

    /// <summary>引导落定等待的降级时限（秒）：超时按已定继续。</summary>
    public int BootstrapSettleTimeoutSeconds { get; init; } = 120;

    /// <summary>导航提交信号等待超时（秒）：超时按已提交继续。</summary>
    public int NavCommitTimeoutSeconds { get; init; } = 5;

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

    /// <summary>从应用旁的 appsettings.json 读取 <c>RuntimeTimeouts</c> 节；文件缺失或节缺失时全默认。</summary>
    public static RuntimeTimeouts Load(string baseDirectory)
    {
        string path = Path.Combine(baseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new RuntimeTimeouts();
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // 配置损坏不阻塞启动：回退全默认（与 UpdateOptions 同哲学——超时是增强配置）。
            return new RuntimeTimeouts();
        }
    }

    /// <summary>把 appsettings.json 全文解析为 <see cref="RuntimeTimeouts"/>（纯函数，可单测）：
    /// 无 <c>RuntimeTimeouts</c> 节、节非对象或键缺失一律回退默认；损坏 JSON 由调用方转 fail-safe。</summary>
    /// <param name="json">appsettings.json 全文。</param>
    /// <returns>解析后的选项（缺省字段保持默认值）。</returns>
    internal static RuntimeTimeouts Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("RuntimeTimeouts", out JsonElement section) ||
            section.ValueKind != JsonValueKind.Object)
        {
            return new RuntimeTimeouts();
        }

        var options = new RuntimeTimeouts();
        options = options with { RelayWaitIntervalSeconds = GetInt(section, nameof(RelayWaitIntervalSeconds), options.RelayWaitIntervalSeconds) };
        options = options with { RelayHelperGraceSeconds = GetInt(section, nameof(RelayHelperGraceSeconds), options.RelayHelperGraceSeconds) };
        options = options with { RelayReadyStableWindowSeconds = GetInt(section, nameof(RelayReadyStableWindowSeconds), options.RelayReadyStableWindowSeconds) };
        options = options with { AdoptedExitPollIntervalSeconds = GetInt(section, nameof(AdoptedExitPollIntervalSeconds), options.AdoptedExitPollIntervalSeconds) };
        options = options with { PortProbeTimeoutMilliseconds = GetInt(section, nameof(PortProbeTimeoutMilliseconds), options.PortProbeTimeoutMilliseconds) };
        options = options with { WebProbeTimeoutMilliseconds = GetInt(section, nameof(WebProbeTimeoutMilliseconds), options.WebProbeTimeoutMilliseconds) };
        options = options with { LifecycleGateWaitSeconds = GetInt(section, nameof(LifecycleGateWaitSeconds), options.LifecycleGateWaitSeconds) };
        options = options with { SpawnTimeoutSeconds = GetInt(section, nameof(SpawnTimeoutSeconds), options.SpawnTimeoutSeconds) };
        options = options with { NotifyPrimaryTimeoutSeconds = GetInt(section, nameof(NotifyPrimaryTimeoutSeconds), options.NotifyPrimaryTimeoutSeconds) };
        options = options with { SupervisorJoinTimeoutSeconds = GetInt(section, nameof(SupervisorJoinTimeoutSeconds), options.SupervisorJoinTimeoutSeconds) };
        options = options with { SupervisorRestartTimeoutSeconds = GetInt(section, nameof(SupervisorRestartTimeoutSeconds), options.SupervisorRestartTimeoutSeconds) };
        options = options with { SupervisorRecoveredRetryDelaySeconds = GetInt(section, nameof(SupervisorRecoveredRetryDelaySeconds), options.SupervisorRecoveredRetryDelaySeconds) };
        options = options with { SupervisorFailedRetryDelaySeconds = GetInt(section, nameof(SupervisorFailedRetryDelaySeconds), options.SupervisorFailedRetryDelaySeconds) };
        options = options with { HealthInitialDelaySeconds = GetInt(section, nameof(HealthInitialDelaySeconds), options.HealthInitialDelaySeconds) };
        options = options with { BootstrapSettleTimeoutSeconds = GetInt(section, nameof(BootstrapSettleTimeoutSeconds), options.BootstrapSettleTimeoutSeconds) };
        options = options with { NavCommitTimeoutSeconds = GetInt(section, nameof(NavCommitTimeoutSeconds), options.NavCommitTimeoutSeconds) };
        options = options with { IpcServeTimeoutSeconds = GetInt(section, nameof(IpcServeTimeoutSeconds), options.IpcServeTimeoutSeconds) };
        options = options with { IpcAcceptRetryDelaySeconds = GetInt(section, nameof(IpcAcceptRetryDelaySeconds), options.IpcAcceptRetryDelaySeconds) };
        options = options with { VersionProbeTimeoutSeconds = GetInt(section, nameof(VersionProbeTimeoutSeconds), options.VersionProbeTimeoutSeconds) };
        options = options with { BannerMaxAttempts = GetInt(section, nameof(BannerMaxAttempts), options.BannerMaxAttempts) };
        options = options with { BannerRetryDelaySeconds = GetInt(section, nameof(BannerRetryDelaySeconds), options.BannerRetryDelaySeconds) };
        options = options with { PushMaxAttempts = GetInt(section, nameof(PushMaxAttempts), options.PushMaxAttempts) };
        options = options with { PushRetryDelayMilliseconds = GetInt(section, nameof(PushRetryDelayMilliseconds), options.PushRetryDelayMilliseconds) };
        return options;
    }

    private static int GetInt(JsonElement section, string name, int current) =>
        section.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : current;
}
