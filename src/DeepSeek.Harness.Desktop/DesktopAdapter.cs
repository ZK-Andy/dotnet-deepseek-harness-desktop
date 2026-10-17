using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 桌面壳 Ryn 适配接线层（Ryn 拥有真正的组合根 <c>RynApplication.CreateBuilder().Build()</c>，
/// 本类只是它的适配器；命名依据与沿革见 ADR split-program-main-god-function、compose-root-form-separation、
/// composition-mechanism-container-assembly）：容器之前的启动头部（沙箱降级 / 运行时与 dev 解析 / 单实例仲裁 / 回环代理）+ 应用装配触发
/// + 启动编排触发（R1 只做装配、启动、接线与兜底）。注册下沉为按域 <c>AddXxx()</c> 扩展，启动编排由容器组装对象图
/// （<see cref="Bootstrap.StartupSequence"/> 构造参数经容器解图、用例经容器供给，本层只显式供给 Build 后应用产出）；应用装配与命令路由注册在唯一的
/// dot 分部 <c>DesktopAdapter.App.cs</c>。
/// </summary>
public sealed partial class DesktopAdapter
{
    // 根字段归零：本类零字段（无状态接线层）。装配期构造数据只经 Run 方法局部流动；
    // 长命共享态（超时/标题栏参数/语言单点/壳转发器）同时进容器单例（见 AddBootstrapSharedState），
    // 编排期由序列自解析——每加一个功能不再加一个字段。

    /// <summary>接线层入口：容器之前解析与仲裁，装配后触发启动编排并返回进程退出码。跨阶段值只经运行期单例
    /// 与本方法局部变量流动，不再经共享接线包。</summary>
    public int Run()
    {
        // WebKit 沙箱 userns 降级（ADR webkit-sandbox-userns-fallback）：必须先于一切 WebView 创建，
        // renderer 进程继承本进程 env；判定在 Core 纯策略，此处仅触发。
        WebkitSandboxFallback.Apply();

        // 装配期共享态与进程级资源各一次创建：前者是值（容器登记同一实例），后者是唯一释放者。
        SharedState shared = PreBuildRegistration.CreateSharedState();
        ShellResources resources = PreBuildRegistration.CreateShellResources();

        try
        {
            // 监督器取消令牌源（寿命 = 本次 Run；释放归 ShellResources，不另起 using 作用域）。
            var supervisorCts = new CancellationTokenSource();
            resources.Attach(supervisorCts);
            // Build 后才就绪的值（窗口/Ryn 应用）：注册闭包只捕获提供者，调用点均晚于赋值。
            RynApplication? builtApp = null;
            CurrentWindowAccessor? builtAccessor = null;

            Preflight preflight = PreBuildRegistration.CreatePreflight(() => builtApp!.Services.GetRequiredService<HostSetup>().Host, () => builtAccessor!, () => shared.UiLocale.IsEnglish, shared);
            if (!AcquireSingleInstance(preflight, () => builtApp, shared.Timeouts, out PrimaryListener? instanceListener))
            {
                return 0;
            }

            resources.Attach(instanceListener);

            // 回环代理源（ADR loopback-forward-proxy）：单实例仲裁后、Build 前启动；绑定失败 loud 后降级 wwwroot，不挡启动。
            DshLoopbackProxy.ProxySetup proxy = StartProxy(shared.ShellForward);
            resources.Attach(proxy);

            (UpdateSetup update, TrayController tray) = PreBuildRegistration.CreateUpdateAndTrayAssembly(
                preflight,
                supervisorCts,
                () => builtApp!.Services.GetRequiredService<IRynWindow>(),
                () => builtAccessor,
                () => builtApp!.Services.GetRequiredService<ExitPipeline>(),
                shared);
            AppSetup app = BuildApp(preflight, proxy.Proxy, update, tray, supervisorCts, instanceListener, shared, resources);
            builtApp = app.App;
            builtAccessor = app.WindowAccessor;
            // 编排触发（容器组装对象图，ADR 组合根机制收官终局的 B3）：唯一显式供给项是 Build 后应用产出
            //（Ryn 不登记 RynApplication）；两相入口的唯一桥在此（C4，ADR entry-bridge-two-phase）。
            Bootstrap.StartupSequence sequence = ActivatorUtilities.CreateInstance<Bootstrap.StartupSequence>(builtApp.Services, app);
            sequence.RunStartupAsync().GetAwaiter().GetResult();
            return sequence.RunMainLoop();
        }
        finally
        {
            // 进程级资源按序释放（host → proxy → cts → listener）；有序退出已由退出管道释放的部分幂等兜底。
            resources.Dispose();
        }
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
                    TimeSpan.FromSeconds(timeouts.IpcAcceptRetryDelaySeconds),
                    TimeSpan.FromSeconds(timeouts.IpcServeTimeoutSeconds),
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
