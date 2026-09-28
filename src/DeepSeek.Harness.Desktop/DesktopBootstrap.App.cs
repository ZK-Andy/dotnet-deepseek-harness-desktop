using Microsoft.Extensions.DependencyInjection;
using Ryn.Callbacks;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的应用装配面（唯一 dot 分部）：Ryn 应用构建、关窗闸门/自更新栈/托盘
/// 控制器装配、命令路由注册（按域下沉为 <c>AddXxx()</c> 扩展，ADR compose-root-form-separation）。
/// 启动头部在 <c>DesktopBootstrap.cs</c>；启动编排已搬出根（<c>Bootstrap.StartupSequence</c>）。
/// </summary>
public sealed partial class DesktopBootstrap
{
    private UpdateSetup InitCloseGateAndUpdateStack(Preflight preflight, StartupWiring wiring)
    {
        // 关窗闸门/自更新栈/托盘控制器装配（原编排阶段 8 随形态分离归位组合根装配面）：构造只收
        // 惰性委托与配置，与运行时产出无交互——装配移至 Build 之前（原 spawn→装配 反转为 装配→spawn，
        // 见 ADR compose-root-form-separation 的等价排列论证）。
        // hide-to-tray 关窗闸门（ADR shell-tray-hide-to-tray）：托盘「退出」与自更新安装路径
        // 先批准再 Close。用户普通关窗是否转隐藏由 closeBehavior 偏好裁决（默认 true 保持
        // 历史行为）；托盘未就绪时拦截不生效（关窗直退）。
        var closeGate = new Tray.CloseGate();
        var closeBehavior = new CloseBehaviorPreference(
            Path.Combine(HarnessRuntimeHost.ResolveDshHome(), CloseBehaviorPreference.FileName));

        // 自更新协调器在此构造并装载（早于 BuildApp）：Application 用例住 Core（ADR
        // update-coordinator-core-port），栈协作经 UpdateStackAdapter 四端口注入（一个适配器同型实现），
        // UI 交接以委托闭包接线（PagePump/横幅/关窗闸门/退出管道）——根只装配，编排策略随用例可单测。
        var updateStack = new UpdateStackAdapter(HostLog.Write);
        var updates = new UpdateCoordinator(
            preflight.Launch.IsDev,
            updateStack,
            updateStack,
            updateStack,
            updateStack,
            () => wiring.SupervisorToken,
            state => PagePump.PushUpdateState(wiring.WindowAccessor, state),
            (version, token) => PagePump.ShowBannerWhenReadyAsync(
                wiring.WindowAccessor!, UpdateBanner.ReadyScript(version, _uiLocale), token),
            closeGate.ApproveExit,
            () => wiring.WindowAccessor?.Current?.Close(),
            ct => wiring.Exit!.ScheduleExitFallback(ct),
            HostLog.Write);
        updates.Load();

        // 托盘控制器在此构造（早于 BuildApp/ShowTray），窗口与 Ryn 服务以惰性委托注入——
        // 控制器持有 hide-to-tray 拦截、唤回采样、菜单重建与关窗闸门/偏好（供路由构造注入）。
        wiring.Tray = new TrayController(
            () => wiring.App!.Services.GetRequiredService<IRynWindow>(),
            () => wiring.WindowAccessor,
            closeGate,
            closeBehavior,
            _uiLocale,
            updates.Machine,
            HostLog.Write);

        return new UpdateSetup(updates);
    }

    private AppSetup BuildApp(Preflight preflight, DshLoopbackProxy? proxy, UpdateSetup update, StartupWiring wiring)
    {
        // 托盘与窗口共用同一 icon 资产；缺失时托盘不注册（关窗保持直退，见 IsReady）
        string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        bool trayAvailable = File.Exists(iconPath); // verify-code-conventions: ignore 组合根装配：icon 存在性探测是配置面，非业务/领域直调
        wiring.Tray!.ConfigureIcon(iconPath, trayAvailable);

        RynApplication app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                // IPC 桥接白名单显式登记代理源（Ryn 默认只认 ryn://app；Ryn dev-server 分支会自动
                // 追加代理源 + IPC 服地址，此处显式更稳，不依赖隐式追加）。
                if (proxy is not null)
                {
                    opts.AllowedOrigins.Add(proxy.Origin);
                }

                if (proxy is not null)
                {
                    // 代理源恒为初始 URL（dsh 未就绪时窗口即开 holder；Ryn dev-server 分支在窗口创建时
                    // 接管 IPC，不依赖 dsh 时序——冷机探针同样有效）。
                    // 仅代理绑定失败才回退 wwwroot 占位（极罕见，行为与旧降级一致）。
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
                // A 类启动配置经类型化值消费（批次 3）：dev 后缀规则封装进 LaunchOptions。
                opts.ApplicationId = preflight.Launch.ApplicationIdFor("io.github.ZK-Andy.dotnet-deepseek-harness-desktop");
                if (File.Exists(iconPath)) // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                {
                    opts.IconPath = iconPath;
                }
                else
                {
                    HostLog.Write($"[host] icon 缺失：{iconPath}");
                }

                HostLog.Write($"[host] Ryn opts: Url={(proxy?.Url?.ToString() ?? "null")} ApplicationId={opts.ApplicationId} Icon={(File.Exists(iconPath) ? iconPath : "missing")}"); // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                // WebView 调试器默认关闭；开发期设 DSH_DEVTOOLS=1 开启（与 dev 判定无关，
                // 打包产品形态下同样生效）——判定随 A 类启动配置在 LaunchOptions.Resolve 单点解析
                // （ADR post-restructure-ledger-batch）。
                opts.DevTools = preflight.Launch.DevTools;
            })
            .ConfigureServices(services => RegisterServices(services, preflight, update, proxy, wiring))
            .Build();

        // 装配产出回填接线槽（托盘控制器/引导页的惰性委托此后可解引用），阶段产出随值流动
        wiring.App = app;
        CurrentWindowAccessor windowAccessor = app.Services.GetRequiredService<CurrentWindowAccessor>();
        wiring.WindowAccessor = windowAccessor;

        return new AppSetup(app, windowAccessor);
    }

    private void RegisterServices(IServiceCollection services, Preflight preflight, UpdateSetup update, DshLoopbackProxy? proxy, StartupWiring wiring)
    {
        services.AddRynCommands();
        services.AddRynCallbacks();
        services.AddRynNavigationCallbacks();
        services.AddExternalLinkRouting(proxy);
        services.AddLocaleCommands(_uiLocale);
        services.AddDiagnosticsCommands(() => wiring.HealthMonitor?.Snapshot);
        services.AddRecoveryCommands(wiring);
        services.AddBootstrapCommands(preflight.Bootstrap);
        services.AddAutostartCommands();
        services.AddTrayServices(update, wiring, _uiLocale);
        services.AddUpdateCommands(update, wiring);
        // 启动编排服务（容器解析；工厂惰性求值——wiring.App/WindowAccessor 在 Build 完成后才回填，
        // 解析时点必晚于 Build，届时求值安全）。
        services.AddSingleton<Core.Bootstrap.IStartupSequence>(_ => new Bootstrap.StartupSequence(
            preflight,
            new AppSetup(wiring.App!, wiring.WindowAccessor!),
            update,
            _uiLocale,
            _timeouts,
            _shellForward,
            proxy,
            wiring,
            _instanceListener));
    }
}
