using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动编排阶段产出（ADR composition-root-value-flow-pipeline 批次 2 的私有嵌套升正式类型，
/// ADR compose-root-form-separation）：阶段方法返回真实产出、消费段收参数——产出的值本身就是执行序证明，
/// 绕过产生阶段即缺值编译失败。值类型归属按拍板 2：跨 R3 边界的 DshWebUrl 进 Core；只在本编排器内
/// 流通的阶段产出留内部类型。值的寿命 = 单段——长命惰性接线在 <see cref="StartupWiring"/>，
/// 不借阶段返回值回填全局态。
/// </summary>
/// <param name="Bootstrap">首启引导服务（全局 node/dsh 引导、插件装配、CLI shim、宿主启动与落定握手）。</param>
/// <param name="Launch">启动期 A 类配置（dev 判定与自动隔离）。</param>
internal readonly record struct Preflight(IFirstBootBootstrap Bootstrap, LaunchOptions Launch);

/// <summary>宿主装配产出（原编排序：宿主创建先于随包插件安装与 dsh spawn——spawn 前置序的偏序承诺）。</summary>
/// <param name="Host">运行时宿主（同时接线 <see cref="StartupWiring.Host"/>，引导服务惰性读）。</param>
/// <param name="Marker">崩溃取证 marker（遗留即判定上轮非受控退出）。</param>
internal readonly record struct HostSetup(HarnessRuntimeHost Host, RunMarkerResult Marker);

/// <summary>自更新栈装配产出（协调器已构造并 <c>Load</c> 装载，早于 BuildApp 的命令路由注册）。</summary>
/// <param name="Updates">自更新协调器（Machine 已按 dev 门禁装载定稿）。</param>
internal readonly record struct UpdateSetup(Update.UpdateCoordinator Updates);

/// <summary>应用装配产出（组合根 <c>Build()</c> 的产物；编排后续阶段的窗口/Ryn 服务来源）。</summary>
/// <param name="App">Ryn 应用实例（主循环宿主）。</param>
/// <param name="WindowAccessor">当前窗口访问器（容器解析；原生建窗前 <c>Current</c> 即抛）。</param>
internal readonly record struct AppSetup(RynApplication App, CurrentWindowAccessor WindowAccessor);

/// <summary>监督器装配产出（Cts 为自更新后台任务与退出管道的取消令牌源，Run 尾部释放）。</summary>
/// <param name="Cts">监督器取消令牌源（同时接线 <see cref="StartupWiring.SupervisorCts"/>）。</param>
/// <param name="Task">监督任务（引导落定门控 + 监督循环；RunAppLoop join）。</param>
internal readonly record struct SupervisorSetup(CancellationTokenSource Cts, Task Task);
