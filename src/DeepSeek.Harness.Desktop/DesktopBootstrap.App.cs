using DeepSeek.Harness.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Callbacks;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的应用装配面（partial，ADR 尺寸健康闸）：Ryn 应用构建、
/// 命令路由/托盘服务注册、托盘就绪化。组合根只装配——注册逻辑集中于此，生命线方法在
/// <c>DesktopBootstrap.cs</c> 与 <c>DesktopBootstrap.Lifecycle.cs</c>。
/// </summary>
public sealed partial class DesktopBootstrap
{
    private AppSetup BuildApp(Preflight preflight, RuntimeSetup runtime, UpdateSetup update)
    {
        // 运行时就位 URL 的消费点（值流）：StartRuntime 产出、此处落位为壳侧导航靶点初值；
        // 引导完成/崩溃恢复导航会再刷新该字段（见 Navigation 与 Lifecycle 的 navigate）。
        _webUrl = runtime.WebUrl?.Value;

        // 托盘与窗口共用同一 icon 资产；缺失时托盘不注册（关窗保持直退，见 IsReady）
        string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        bool trayAvailable = File.Exists(iconPath); // verify-code-conventions: ignore 组合根装配：icon 存在性探测是配置面，非业务/领域直调
        _tray.ConfigureIcon(iconPath, trayAvailable);

        _app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                if (runtime.WebUrl is { } webUrl)
                {
                    // dsh web UI（loopback；完整运行时随应用内置后仍是此路径）
                    opts.Url = webUrl.Value;
                }
                else
                {
                    // 降级：dsh 未起时展示本地占位页，保证壳仍可开
                    opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                }

                opts.Title = "DeepSeek Harness Desktop";
                opts.Width = 1200;
                opts.Height = 800;
                opts.ApplicationId = DevEnvironment.ApplicationIdFor(
                    "io.github.ZK-Andy.dotnet-deepseek-harness-desktop", preflight.IsDev);
                if (File.Exists(iconPath)) // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                {
                    opts.IconPath = iconPath;
                }
                else
                {
                    HostLog.Write($"[host] icon 缺失：{iconPath}");
                }

                HostLog.Write($"[host] Ryn opts: Url={(runtime.WebUrl is { } logged ? logged.ToString() : "null")} ApplicationId={opts.ApplicationId} Icon={(File.Exists(iconPath) ? iconPath : "missing")}"); // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                // WebView 调试器默认关闭（正式打包无调试窗口）；开发期设 DSH_DEVTOOLS=1 开启。
                opts.DevTools = Environment.GetEnvironmentVariable("DSH_DEVTOOLS") == "1";
            })
            .ConfigureServices(services => RegisterServices(services, preflight, runtime, update))
            .Build();

        _windowAccessor = _app.Services.GetRequiredService<CurrentWindowAccessor>();

        return new AppSetup(_app, _windowAccessor);
    }

    private void RegisterServices(IServiceCollection services, Preflight preflight, RuntimeSetup runtime, UpdateSetup update)
    {
        services.AddRynCommands();
        // 宿主导航回调（Ryn 0.32.0 Ryn.Callbacks）：在导航边界统一拦截外部链接（ADR ryn-navigation-callbacks）。
        services.AddRynCallbacks();
        services.AddRynNavigationCallbacks();
        // 覆盖源生成的 handler 无参注册：导航回调依赖（openExternal 打开器 / 日志 /
        // 当前页面 origin）在 ConfigureServices 时已知，经工厂注入。
        services.AddSingleton(sp => new Services.RynNavigationCallbacks(
            opener: null,
            log: HostLog.Write,
            currentOrigin: runtime.WebUrl?.Authority,
            // 外部链接打开失败 → 推事件给页面，companion 渲染 toast（R2 N2）。EmitEvent 走
            // deferred IRynWebView（窗口就绪后转发），在导航回调触发时页面必然已加载。
            notifyLinkFail: url => sp.GetRequiredService<IRynWebView>().EmitEvent(
                "desktop.externalLinkOpenerFailed",
                new Services.ExternalLinkOpenerFailedFrame(url),
                Services.AppJsonContext.Default.ExternalLinkOpenerFailedFrame)));
        // 外部链接 → 系统默认浏览器（宿主命令路由，见 implemented ADR open-external-links-in-system-browser）
        services.AddSingleton<ICommandRouter>(new Services.ExternalLinkCommandRouter(log: HostLog.Write));
        // dsh 语言变更桥接（desktop.companion.setLocale，ADR host-ui-locale）
        services.AddSingleton<ICommandRouter>(new Services.CompanionLocaleCommandRouter(_uiLocale, log: HostLog.Write));
        // 诊断包导出（desktop.diagnostics.export；ryn.json 的 desktop 能力面已放行）
        services.AddSingleton<ICommandRouter>(new Services.DesktopDiagnosticsCommandRouter(
            log: HostLog.Write, healthSnapshot: () => _healthMonitor?.Snapshot));
        // 恢复页退出（desktop.recovery.exit）：先批准关窗闸门再 Close——hide-to-tray 拦截下
        // 未批准的 Close 会吞成隐藏；顺序契约与托盘退出同款（ADR diag-masking-and-recovery-page）
        services.AddSingleton<ICommandRouter>(sp => new Services.RecoveryCommandRouter(
            closeWindow: () => sp.GetRequiredService<IRynWindow>().Close(),
            _tray.CloseGate,
            HostLog.Write));
        // 引导重试命令（desktop.bootstrap.retry，ADR online-first-unbundled-runtime）：
        // wwwroot 引导页的重试按钮 → 闸门放行引导循环。gate 实例在 Run 顶部创建，
        // 引导任务与路由共用同一实例
        services.AddSingleton<ICommandRouter>(new Services.BootstrapCommandRouter(
            preflight.Bootstrap.Gate, HostLog.Write));
        // 插件引导决策命令（desktop.preinstall.choose，ADR reference-alignment 批次二）：
        // wwwroot 引导页「插件引导」步的确认装/跳过 → 闸门放行引导任务
        services.AddSingleton<ICommandRouter>(new Services.PreinstallCommandRouter(
            preflight.Bootstrap.PreinstallGate, HostLog.Write));
        // 开机自启开关（desktop.autostart.getState/set）
        services.AddSingleton<ICommandRouter>(new Services.AutostartCommandRouter(log: HostLog.Write));
        // 关闭最小化到托盘偏好（desktop.closeToTray.getState/set）；available 惰性求值——
        // 服务注册早于托盘初始化，trayReady 由外层闭包稍后赋值
        services.AddSingleton<ICommandRouter>(new Services.Tray.CloseToTrayCommandRouter(
            _tray.CloseBehavior, () => _tray.IsReady, log: HostLog.Write));
        // 自更新命令：desktop.update.getState / check / install（dev 门禁下不注册路由，invoke 自然失败）
        if (update.Updates.Machine is { } updateMachine)
        {
            services.AddSingleton<ICommandRouter>(new Services.Update.DesktopUpdateCommandRouter(updateMachine, log: HostLog.Write, backgroundToken: () => _supervisorCtsRef?.Token ?? CancellationToken.None));
        }
        RegisterTrayServices(services, update);
    }

    /// <summary>托盘服务注册（ADR shell-tray-hide-to-tray）：图标+菜单 + 事件路由（窗口动作经委托接 deferred 代理）。</summary>
    private void RegisterTrayServices(IServiceCollection services, UpdateSetup update)
    {
        // 托盘（批次三）：图标+菜单；点击语义经 companion 中继
        // 回 desktop.tray.event 在宿主解析——EmitEvent 是 Ryn 插件内部属性，不在源生成通道
        if (_tray.IsAvailable)
        {
            services.AddRynTray(o =>
            {
                o.IconPath = _tray.IconPath;
                o.Tooltip = "DeepSeek Harness Desktop";
            });
        }
        // 托盘事件路由：窗口动作经委托接 deferred 代理（注册期无需窗口就绪；
        // 委托注入让退出顺序契约可用记序 fake 测试）
        services.AddSingleton<ICommandRouter>(sp => new Services.Tray.DesktopTrayCommandRouter(
            showWindow: () => _tray.RecallAsync(),
            closeWindow: () =>
            {
                // 管道在 supervisorCts 声明后接线，而托盘退出必经托盘菜单的用户交互、
                // 必然晚于接线，故此处不可能为 null
                _exit.OrderlyQuit();
            },
            _tray.CloseGate,
            update.Updates.Machine,
            HostLog.Write,
            notify: (title, message) =>
                sp.GetRequiredService<TrayService>().ShowNotification(title, message)));
    }

    /// <summary>托盘就绪化（装配壳）：解析 Ryn 托盘服务并交给控制器（无托盘环境传 null）。</summary>
    private void ShowTray(AppSetup app)
    {
        _tray.Show(_tray.IsAvailable
            ? () => app.App.Services.GetRequiredService<TrayService>()
            : null);
    }
}
