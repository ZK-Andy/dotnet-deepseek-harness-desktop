using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动编排阶段产出（ADR composition-root-value-flow-pipeline 批次 2 的私有嵌套升正式类型，
/// ADR compose-root-form-separation）：阶段方法返回真实产出、消费段收参数——产出的值本身就是执行序证明，
/// 绕过产生阶段即缺值编译失败。值类型归属按拍板 2：跨 R3 边界的 DshWebUrl 进 Core；只在本编排器内
/// 流通的阶段产出留内部类型。值的寿命 = 单段——长命值走容器单例（宿主/退出管道/健康观测），
/// 不借阶段返回值回填全局态。
/// </summary>
/// <param name="Bootstrap">首启引导服务（全局 node/dsh 引导、插件装配、CLI shim、宿主启动与落定握手）。</param>
/// <param name="Launch">启动期 A 类配置（dev 判定与自动隔离）。</param>
internal readonly record struct Preflight(IFirstBootBootstrap Bootstrap, LaunchOptions Launch);

/// <summary>宿主装配产出（容器惰性单例，见 RunServicesRegistration.AddRunHost；原编排序：宿主创建先于随包插件安装与 dsh spawn——spawn 前置序的偏序承诺）。</summary>
/// <param name="Host">运行时宿主（容器单例；引导服务经提供者惰性读）。</param>
/// <param name="Marker">崩溃取证 marker（遗留即判定上轮非受控退出）。</param>
internal readonly record struct HostSetup(HarnessRuntimeHost Host, RunMarkerResult Marker);

/// <summary>自更新栈装配产出（协调器已构造并 <c>Load</c> 装载，早于 BuildApp 的命令路由注册）。</summary>
/// <param name="Updates">自更新协调器（Machine 已按 dev 门禁装载定稿；Application 用例住 Core，ADR update-coordinator-core-port）。</param>
internal readonly record struct UpdateSetup(UpdateCoordinator Updates);

/// <summary>应用装配产出（装配面 <c>Build()</c> 的产物；编排后续阶段的窗口/Ryn 服务来源）。</summary>
/// <param name="App">Ryn 应用实例（主循环宿主）。</param>
/// <param name="WindowAccessor">当前窗口访问器（容器解析；原生建窗前 <c>Current</c> 即抛）。</param>
internal readonly record struct AppSetup(RynApplication App, CurrentWindowAccessor WindowAccessor);

/// <summary>单实例监听器持有（ADR 组合根机制收官终局的 B3）：DI 表达不了可空实例，故以值记录过容器
/// ——编排尾部释放锁地址（非 orderly 退出路径；有序路径由退出管道释放，幂等守卫保证重复安全）。</summary>
/// <param name="Listener">单实例监听器；null = 未启用（已有主实例/平台不支持/降级）。</param>
internal readonly record struct InstanceListenerSetup(PrimaryListener? Listener);

/// <summary>装配期共享态（ADR 组合根寿命与配置单一来源 D1）：容器之前一次造好的配置与注入单点，前 Build
/// 消费与编排期容器解析拿到同一实例；<see cref="Timeouts"/> 同时是配置模型的唯一创建点（D4）。</summary>
/// <param name="Timeouts">运行时超时/重试配置（唯一 <c>Load</c> 点）。</param>
/// <param name="CaptionBar">自绘标题栏参数家。</param>
/// <param name="UiLocale">宿主 UI 语言单点。</param>
/// <param name="ShellForward">壳转发器（铸币态唯一家）。</param>
/// <param name="Pump">页面注入单点（重试预算取自 <paramref name="Timeouts"/>；装配期与编排期共用同一实例）。</param>
/// <param name="ScriptTarget">当前窗口脚本求值端口（访问器惰性供给；装配期与容器登记同一实例，
/// ADR architecture/2026-10-21-window-script-target-port）。</param>
internal sealed record SharedState(
    RuntimeTimeouts Timeouts,
    CaptionBarOptions CaptionBar,
    UiLocale UiLocale,
    DshShellForward ShellForward,
    PagePump Pump,
    IWindowScriptTarget ScriptTarget);
