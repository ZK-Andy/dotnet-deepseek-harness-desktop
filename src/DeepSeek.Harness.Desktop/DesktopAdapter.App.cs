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
    private AppSetup BuildApp(Preflight preflight, DshLoopbackProxy? proxy, UpdateSetup update, TrayController tray, CancellationTokenSource supervisorCts, PrimaryListener? instanceListener, SharedState shared, ShellResources resources)
    {
        // 托盘与窗口共用同一 icon 资产；缺失时托盘不注册（关窗保持直退，见 IsReady）
        string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        bool trayAvailable = File.Exists(iconPath); // verify-code-conventions: ignore 组合根装配：icon 存在性探测是配置面，非业务/领域直调
        tray.ConfigureIcon(iconPath, trayAvailable);

        RynApplication app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts => RynOptionsProfile.Apply(opts, preflight, proxy, shared.CaptionBar, iconPath))
            .ConfigureServices(services => RegisterServices(services, preflight, update, proxy, tray, supervisorCts, instanceListener, shared, resources))
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
        SharedState shared,
        ShellResources resources)
    {
        services.AddRynCommands();
        services.AddRynCallbacks();
        services.AddRynNavigationCallbacks();
        services.AddExternalLinkRouting(proxy);
        services.AddLocaleCommands(shared.UiLocale);
        services.AddCompanionReportCommand();
        services.AddDiagnosticsCommands();
        services.AddFailureSurface();
        services.AddRecoveryCommands();
        services.AddBootstrapCommands(preflight.Bootstrap);
        services.AddAutostartCommands();
        services.AddAppRestartCommand();
        services.AddSingleton(tray);
        services.AddSingleton(supervisorCts);
        services.AddBootstrapSharedState(shared);
        // 启动前置阶段值（ADR 组合根机制收官终局的 B3）：编排器构造参数改由容器解图，
        // 接线层不再逐个手传。
        services.AddStartupStageValues(preflight, update);
        // 启动用例与三个新端口的适配器（ADR 组合根机制收官 step-1）：导航/监督用例经容器供给，
        // 接线层只解析触发（R1）。
        services.AddStartupUseCases(preflight.Bootstrap, proxy, shared.Pump);
        // 运行时起步用例（ADR 组合根机制收官终态装配）：spawn→铸币→收 URL 的依赖全在手
        // （引导服务来自启动头部，超时/铸币来自共享单例），构造住 Bootstrap.PreBuildRegistration。
        services.AddSingleton<IRuntimeStarter>(PreBuildRegistration.CreateRuntimeStarter(preflight, shared.Timeouts, shared.ShellForward));
        services.AddRunHost(resources);
        services.AddExitPipeline(supervisorCts, instanceListener);
        services.AddHealthMonitor(proxy);
        services.AddTrayServices(update, tray, shared.UiLocale);
        services.AddUpdateCommands(update);
    }
}
