namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动阶段产出（ADR compose-root-form-separation）：Avalonia 宿主启动前解析的两个值对——首启引导服务
/// 与启动期 A 类配置。值类型归属按拍板 2：跨 R3 边界的 <c>DshWebUrl</c> 进 Core；只在本编排器内流通的
/// 阶段产出留内部类型。值的寿命 = 单段——长命值走装配期显式传递，不借阶段返回值回填全局态。
/// </summary>
/// <param name="Bootstrap">首启引导服务（全局 node/dsh 引导、插件装配、CLI shim、宿主启动与落定握手）。</param>
/// <param name="Launch">启动期 A 类配置（dev 判定与自动隔离）。</param>
internal readonly record struct Preflight(IFirstBootBootstrap Bootstrap, LaunchOptions Launch);

/// <summary>装配期共享态（ADR 组合根寿命与配置单一来源 D1）：容器之前一次造好的配置与注入单点，
/// 启动头部与窗口/桥装配拿到同一实例；<see cref="Timeouts"/> 同时是配置模型的唯一创建点（D4）。</summary>
/// <param name="Timeouts">运行时超时/重试配置（唯一 <c>Load</c> 点）。</param>
/// <param name="CaptionBar">自绘标题栏参数家。</param>
/// <param name="UiLocale">宿主 UI 语言单点。</param>
/// <param name="ShellForward">壳转发器（铸币态唯一家）。</param>
/// <param name="Pump">页面注入单点（重试预算取自 <paramref name="Timeouts"/>；装配期与运行期共用同一实例）。</param>
internal sealed record SharedState(
    RuntimeTimeouts Timeouts,
    CaptionBarOptions CaptionBar,
    UiLocale UiLocale,
    DshShellForward ShellForward,
    PagePump Pump);
