using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 排序器运行期解析面（ADR sequencer-resolution-injection）：把运行期解析收成一份不可变构造数据、由注册处装配，
/// 排序器据此不再持 <c>IServiceProvider</c>。两项是**硬时点约束**、必须 <see cref="Lazy{T}"/> 携带——
/// <see cref="CaptionBarInstaller"/> 构造即取 <c>IRynWebView</c>（原生建窗前不可碰）、
/// <see cref="TrayService"/> 仅在托盘可用时注册（不可用时解析即抛）。其余七项今天由装配面更早解析或构造
/// （共享态/宿主的 eager 解析、装配期已建实例、纯赋值构造体），对它们的 Lazy 是**防御性**的：它把
/// 「首次解析仍落在原调用点」写成结构承诺，根侧解析顺序一变也不回退。平铺九个 <see cref="Lazy{T}"/>
/// 构造参数会把排序器构造表推到 18 项（母篇 Problem 记的正是构造表膨胀），故收成一份数据；
/// 解析面再长时按启动阶段拆组。
/// </summary>
/// <param name="Profile">桌面 profile 前置用例（migrate→recover→ensure→reconcile）。</param>
/// <param name="Companion">随包插件 spawn 前安装用例。</param>
/// <param name="Host">宿主装配产出（首次解析即建宿主；须晚于 profile 前置、早于随包安装与 spawn）。</param>
/// <param name="Supervision">运行时监督用例（经宿主派生，构造期即取宿主）。</param>
/// <param name="ExitPipeline">退出管道（经宿主派生；非 orderly 退出路径复用）。</param>
/// <param name="MainUiEntry">进入主界面用例（引导就位回调的目标）。</param>
/// <param name="CaptionBarInstaller">无边框顶栏注入用例（窗口就绪后才可解析）。</param>
/// <param name="StartupNotices">启动期告知用例（经宿主 marker 派生）。</param>
/// <param name="HealthMonitor">页面健康观测（窗口访问器只读探针）。</param>
/// <param name="Tray">托盘控制器（装配期构造、随装配面注册的实例）。</param>
/// <param name="TrayService">Ryn 托盘服务（仅在托盘可用时经委托取用）。</param>
internal sealed record RuntimeResolutions(
    ProfileLifecycle Profile,
    CompanionPreSpawn Companion,
    Lazy<HostSetup> Host,
    Lazy<Core.Bootstrap.RuntimeSupervision> Supervision,
    Lazy<ExitPipeline> ExitPipeline,
    Lazy<Core.Ui.MainUiEntry> MainUiEntry,
    Lazy<CaptionBarInstaller> CaptionBarInstaller,
    Lazy<StartupNoticeService> StartupNotices,
    Lazy<PageHealthMonitor> HealthMonitor,
    Lazy<Tray.TrayController> Tray,
    Lazy<TrayService> TrayService);
