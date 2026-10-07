using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 桌面壳组合根（ADR split-program-main-god-function；形态分离见 ADR compose-root-form-separation）：
/// 只装配、启动、接线与兜底（R1）。注册下沉为按域 <c>AddXxx()</c> 扩展、启动编排搬出根成显式组装的
/// <see cref="Bootstrap.StartupSequence"/>（容器只给零件：post-Build 值 + 运行期单例）——本文件只保留
/// 容器之前的启动头部（沙箱降级 / 运行时与 dev 解析 / 单实例仲裁 / 回环代理），应用装配与命令路由
/// 注册在唯一的 dot 分部 <c>DesktopBootstrap.App.cs</c>。
/// </summary>
public sealed partial class DesktopBootstrap
{
    // 2b 根字段归零：本类零字段（无状态接线层）。装配期构造数据只经 Run 方法局部流动；
    // 长命共享态（超时/标题栏参数/语言单点/壳转发器）同时进容器单例（见 AddBootstrapSharedState），
    // 编排期调用点经容器解析——每加一个功能不再加一个字段（代价：BuildApp/RegisterServices 签名各 +4
    // 参数，终态随 step-3 容器自组装收回）。

    /// <summary>组合根入口：容器之前解析与仲裁，装配后把启动编排交显式组装的
    /// <see cref="Bootstrap.StartupSequence"/> 并返回进程退出码。跨阶段值只经运行期单例
    /// 与本方法局部变量流动，不再经共享接线包。</summary>
    public int Run()
    {
        // WebKit 沙箱 userns 降级（ADR webkit-sandbox-userns-fallback）：必须先于一切 WebView 创建，
        // renderer 进程继承本进程 env；判定在 Core 纯策略，此处仅触发。
        WebkitSandboxFallback.Apply();

        // 装配期共享态一次创建（见 ResolveSharedState）：前 Build 消费走局部，编排期经容器单例（同一实例）。
        (RuntimeTimeouts timeouts, CaptionBarOptions captionBar, UiLocale uiLocale, DshShellForward shellForward) = ResolveSharedState();

        // 监督器取消令牌源（寿命 = 本次 Run；释放随本方法作用域，见末尾 using 语义）。
        using var supervisorCts = new CancellationTokenSource();
        // Build 后才就绪的值（窗口/Ryn 应用）：注册闭包只捕获提供者，调用点均晚于赋值。
        RynApplication? builtApp = null;
        CurrentWindowAccessor? builtAccessor = null;

        Preflight preflight = ResolveRuntimeAndDev(() => builtApp!.Services.GetRequiredService<HostSetup>().Host, () => builtAccessor!, () => uiLocale.IsEnglish);
        if (!AcquireSingleInstance(preflight, () => builtApp, timeouts, out PrimaryListener? instanceListener))
        {
            return 0;
        }

        // 回环代理源（ADR loopback-forward-proxy）：单实例仲裁后、Build 前启动；绑定失败 loud 后降级 wwwroot，不挡启动。
        DshLoopbackProxy.ProxySetup proxy = StartProxy(shellForward);
        HostSetup? hostSetup = null;
        try
        {
            (UpdateSetup update, TrayController tray) = InitCloseGateAndUpdateStack(
                preflight,
                supervisorCts,
                () => builtApp!.Services.GetRequiredService<IRynWindow>(),
                () => builtAccessor,
                () => builtApp!.Services.GetRequiredService<ExitPipeline>(),
                uiLocale);
            AppSetup app = BuildApp(preflight, proxy.Proxy, update, tray, supervisorCts, instanceListener, uiLocale, captionBar, timeouts, shellForward);
            builtApp = app.App;
            builtAccessor = app.WindowAccessor;
            // 宿主 + 崩溃标记（容器惰性单例，见 RunServicesRegistration.AddRunHost）：首次解析即创建；
            // 脏退播报在此（首次解析点之后；工厂只做构造，见 R2S3）。
            hostSetup = builtApp.Services.GetRequiredService<HostSetup>();
            if (hostSetup.Value.Marker.PreviousRunUnclean)
            {
                HostLog.Write("[host] 检测到上轮未正常退出的标记；如频繁出现请在设置页导出诊断信息");
            }
            // 编排服务显式组装（容器只给零件）：共享态经容器回读（注册源即本方法局部，同一实例，单实例唯一）；
            // hostSetup 必已赋值——解析失败即抛，抛后直接进 finally，走不到组装。
            IServiceProvider container = builtApp.Services;
            var sequence = new Bootstrap.StartupSequence(preflight, app, update, container.GetRequiredService<UiLocale>(), container.GetRequiredService<RuntimeTimeouts>(), container.GetRequiredService<CaptionBarOptions>(), container.GetRequiredService<DshShellForward>(), proxy.Proxy, instanceListener);
            return sequence.Run();
        }
        finally
        {
            // 代理资源的释放（cancel → dispose cts → dispose proxy）随结果对象收口；
            // 宿主释放随编排产出收口（原编排 Run finally 语义上移至此）。
            hostSetup?.Host.Dispose();
            proxy.Dispose();
        }
    }

    /// <summary>装配期共享态一次创建（2b 根字段归零：替代四个根字段）：运行时超时家（与 A 类启动配置同点解析
    /// 一次，见 <c>RuntimeTimeouts</c>）+ 自绘标题栏参数家（ADR frameless-uniform-caption-bar）+ 宿主 UI 语言
    /// 单点（ADR host-ui-locale：上次上报值经 profile 目录持久化，dsh 起来前也能取到）+ 壳转发器（构造即备好
    /// HttpClient，无 I/O）。纯创建无时序依赖；长命共享态同时进容器单例（见 AddBootstrapSharedState）。</summary>
    private static (RuntimeTimeouts Timeouts, CaptionBarOptions CaptionBar, UiLocale UiLocale, DshShellForward ShellForward) ResolveSharedState() =>
    (
        RuntimeTimeouts.Load(AppContext.BaseDirectory, HostLog.Write),
        CaptionBarOptions.Load(AppContext.BaseDirectory, HostLog.Write),
        new UiLocale(new DesktopUiLocaleStore(HostLog.Write)),
        new DshShellForward()
    );

    private static Preflight ResolveRuntimeAndDev(
        Func<HarnessRuntimeHost> hostProvider,
        Func<CurrentWindowAccessor> accessorProvider,
        Func<bool> isEnglishProvider)
    {
        // 首启引导服务（R3 端口实现，ADR composition-root-value-flow-pipeline 批次 1）：全局 node/dsh
        // 引导、插件装配、CLI shim、宿主启动从组合根下沉；页面反馈经 FirstBootUi 注入，宿主/窗口以
        // 提供者延迟供给（调用点均晚于 Build 赋值，与原接线槽「注册早于赋值」语义等价）。
        // UI 语言同样惰性：isEnglishProvider 在引导任务实际启动时求值，调用点必已就绪。
        IFirstBootBootstrap bootstrap = new FirstBootBootstrapService(
            hostProvider,
            new FirstBootUi(accessorProvider),
            isEnglishProvider,
            HostLog.Write);
        bootstrap.Resolve();

        // A 类启动配置（批次 3 IOptions 化）：dev 判定与自动隔离下沉 Infrastructure（LaunchOptions.Resolve），
        // 组合根不再散读 dev 标记环境变量。
        return new Preflight(bootstrap, LaunchOptions.Resolve(HostLog.Write));
    }

    /// <summary>单实例仲裁（ADR single-instance-launcher-activation）：false = 已有主实例，调用方直接返回 0。
    /// 锁地址解析（XDG 回退/uid 隔离/dev 分域等平台策略）住 Infrastructure
    /// <see cref="LauncherActivation.ResolveInstanceSocketPath"/>；此处只仲裁。</summary>
    private static bool AcquireSingleInstance(
        Preflight preflight,
        Func<RynApplication?> appProvider,
        RuntimeTimeouts timeouts,
        out PrimaryListener? instanceListener)
    {
        string? instanceSocketPath = LauncherActivation.ResolveInstanceSocketPath(preflight.Launch.IsDev);
        if (instanceSocketPath is not null)
        {
            if (!LauncherActivation.TryBindPrimary(
                    instanceSocketPath,
                    onShowRequested: async () =>
                    {
                        // 托盘控制器在应用装配后才进容器：启动极早期到达的激活请求
                        // （应用尚未 Build）静默忽略（控制器与窗口均未就绪）
                        RynApplication? app = appProvider();
                        if (app is null)
                        {
                            return;
                        }

                        await app.Services.GetRequiredService<TrayController>().ActivateFromLauncherAsync();
                    },
                    HostLog.Write,
                    out instanceListener))
            {
                bool notified = LauncherActivation.NotifyPrimary(instanceSocketPath, TimeSpan.FromSeconds(timeouts.NotifyPrimaryTimeoutSeconds));
                HostLog.Write(
                    $"[host] 已有主实例在运行（launcher 二次启动）：通知显示主窗{(notified ? "成功" : "未达（主实例可能正忙）")}，本次启动退出");
                instanceListener = null;
                return false;
            }
        }
        else
        {
            instanceListener = null;
        }

        return true;
    }

    /// <summary>启动回环代理源（ADR loopback-forward-proxy）：绑定失败 loud 后降级
    /// （窗口走 wwwroot，行为与 dsh 未起一致，不挡启动）。引导静态根随代理装配
    /// （未铸币时本地 holder/指南面；Ryn IPC 自窗口创建即活，不依赖 dsh 时序）。
    /// 产出结果对象（组合根值流：消费段收参；绑定异常类型清单属协议/平台策略，在 Infrastructure）。</summary>
    private static DshLoopbackProxy.ProxySetup StartProxy(DshShellForward shellForward) =>
        DshLoopbackProxy.TryCreate(shellForward, Path.Combine(AppContext.BaseDirectory, "wwwroot"), HostLog.Write);
}
