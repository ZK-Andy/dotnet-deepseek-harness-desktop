using Microsoft.Extensions.DependencyInjection;
using Ryn.Callbacks;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopAdapter"/> 的应用装配面（唯一 dot 分部）：Ryn 应用构建与命令路由注册（按域下沉为
/// <c>AddXxx()</c> 扩展，ADR compose-root-form-separation）；关窗闸门/自更新栈/托盘控制器装配与引导装配
/// 搬进 <c>Bootstrap/PreBuildRegistration.cs</c>（ADR sequencer-resolution-injection 的 C3 压舱）。
/// 启动头部在 <c>DesktopAdapter.cs</c>；启动编排由容器组装（<c>Bootstrap.StartupSequence</c> 自解析）。
/// </summary>
public sealed partial class DesktopAdapter
{
    private AppSetup BuildApp(Preflight preflight, DshLoopbackProxy? proxy, UpdateSetup update, TrayController tray, CancellationTokenSource supervisorCts, PrimaryListener? instanceListener, UiLocale uiLocale, CaptionBarOptions captionBar, RuntimeTimeouts timeouts, DshShellForward shellForward)
    {
        // 托盘与窗口共用同一 icon 资产；缺失时托盘不注册（关窗保持直退，见 IsReady）
        string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        bool trayAvailable = File.Exists(iconPath); // verify-code-conventions: ignore 组合根装配：icon 存在性探测是配置面，非业务/领域直调
        tray.ConfigureIcon(iconPath, trayAvailable);

        RynApplication app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                // IPC 桥接白名单显式登记代理源（Ryn 默认只认 ryn://app；Ryn dev-server 分支会自动
                // 追加代理源 + IPC 服地址，此处显式更稳，不依赖隐式追加）。代理源恒为初始 URL
                // （dsh 未就绪时窗口即开 holder；Ryn dev-server 分支在窗口创建时接管 IPC，不依赖
                // dsh 时序——冷机探针同样有效）。仅代理绑定失败才回退 wwwroot 占位（极罕见）。
                if (proxy is not null)
                {
                    opts.AllowedOrigins.Add(proxy.Origin);
                    opts.Url = proxy.Url;
                }
                else
                {
                    // 降级：代理未起时展示本地占位页，保证壳仍可开
                    opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                }

                opts.Title = "DeepSeek Harness Desktop";
                opts.Width = 1200;
                opts.Height = 800;
                // 无边框窗口（ADR frameless-uniform-caption-bar）：Frameless 三端去原生 chrome；macOS 观感
                // 由壳自绘（注入脚本画红绿灯 + 侧栏顶部让位，见 PageBridge.CaptionBar），拖拽条与 chrome
                // 同高。禁用 Overlay/TrafficLightPosition（原生红绿灯，mac x64 Rosetta segfault）与任何
                // 宿主冒充（data-platform 会令 dsh 客户端插件加载失败）。
                opts.TitleBarStyle = TitleBarStyle.Frameless;
                opts.TitleBarAutoDragHeight = captionBar.HeightPx;
                opts.Backdrop = MacNativeChrome.IsEnabled ? BackdropMaterial.Blur : BackdropMaterial.None; // macOS 毛玻璃（官方 vibrancy）；闸外恒 None（判据单点见 MacNativeChrome）
                opts.PersistWindowState = true; // 几何持久化：rationale 见 ADR window-geometry-ryn-native-persist
                // Linux 帧传递路径不在此赋值：Ryn 的 Build 从 appsettings 的 Ryn:LinuxRenderingMode 绑定
                // （ADR webview-shared-memory-rendering），回调里再写一遍会静默压掉那个框架键。
                // A 类启动配置经类型化值消费（批次 3）：dev 后缀规则封装进 LaunchOptions。
                opts.ApplicationId = preflight.Launch.ApplicationIdFor("io.github.ZK-Andy.dotnet-deepseek-harness-desktop");
                if (File.Exists(iconPath)) // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                {
                    opts.IconPath = iconPath;
                }
                // 缺失不告警分支：icon 缺失由下方 Ryn opts 摘要行的 Icon=missing 留痕（托盘侧 IsReady 同样降级）

                HostLog.Write($"[host] Ryn opts: Url={(proxy?.Url?.ToString() ?? "null")} ApplicationId={opts.ApplicationId} LinuxRendering={opts.LinuxRenderingMode} ForceShmEnv={Environment.GetEnvironmentVariable("WEBKIT_DMABUF_RENDERER_FORCE_SHM") ?? "-"} Icon={(File.Exists(iconPath) ? iconPath : "missing")}"); // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                // WebView 调试器默认关闭；开发期设 DSH_DEVTOOLS=1 开启（与 dev 判定无关，
                // 打包产品形态下同样生效）——判定随 A 类启动配置在 LaunchOptions.Resolve 单点解析
                // （ADR post-restructure-ledger-batch）。
                opts.DevTools = preflight.Launch.DevTools;
            })
            .ConfigureServices(services => RegisterServices(services, preflight, update, proxy, tray, supervisorCts, instanceListener, uiLocale, captionBar, timeouts, shellForward))
            .Build();

        // 装配产出随值流动：窗口访问器由容器解析，调用方收进方法局部（不再回填共享槽）。
        CurrentWindowAccessor windowAccessor = app.Services.GetRequiredService<CurrentWindowAccessor>();

        return new AppSetup(app, windowAccessor);
    }

    private void RegisterServices(
        IServiceCollection services,
        Preflight preflight,
        UpdateSetup update,
        DshLoopbackProxy? proxy,
        TrayController tray,
        CancellationTokenSource supervisorCts,
        PrimaryListener? instanceListener,
        UiLocale uiLocale,
        CaptionBarOptions captionBar,
        RuntimeTimeouts timeouts,
        DshShellForward shellForward)
    {
        services.AddRynCommands();
        services.AddRynCallbacks();
        services.AddRynNavigationCallbacks();
        services.AddExternalLinkRouting(proxy);
        services.AddLocaleCommands(uiLocale);
        services.AddCompanionReportCommand();
        services.AddDiagnosticsCommands();
        services.AddFailureSurface();
        services.AddRecoveryCommands();
        services.AddBootstrapCommands(preflight.Bootstrap);
        services.AddAutostartCommands();
        services.AddAppRestartCommand();
        services.AddSingleton(tray);
        services.AddSingleton(supervisorCts);
        services.AddBootstrapSharedState(timeouts, captionBar, uiLocale, shellForward);
        // 启动前置阶段值（ADR 组合根机制收官终局的 B3）：编排器构造参数改由容器解图，
        // 接线层不再逐个手传。
        services.AddStartupStageValues(preflight, update);
        // 启动用例与三个新端口的适配器（ADR 组合根机制收官 step-1）：导航/监督用例经容器供给，
        // 接线层只解析触发（R1）。
        services.AddStartupUseCases(preflight.Bootstrap, proxy);
        // 运行时起步用例（ADR 组合根机制收官终态装配）：spawn→铸币→收 URL 的依赖全在手
        // （引导服务来自启动头部，超时/铸币来自共享单例），构造住 Bootstrap.PreBuildRegistration。
        services.AddSingleton<IRuntimeStarter>(PreBuildRegistration.CreateRuntimeStarter(preflight, timeouts, shellForward));
        services.AddRunHost();
        services.AddExitPipeline(supervisorCts, instanceListener);
        services.AddHealthMonitor(proxy);
        services.AddTrayServices(update, tray, uiLocale);
        services.AddUpdateCommands(update);
    }
}
