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
    private AppToken BuildApp(RuntimeToken runtime, UpdateToken update)
    {
        // 托盘与窗口共用同一 icon 资产；缺失时托盘不注册（关窗保持直退，见 trayReady）
        _iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        _trayAvailable = File.Exists(_iconPath); // verify-code-conventions: ignore 组合根装配：icon 存在性探测是配置面，非业务/领域直调

        _app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                if (_webUrl is not null)
                {
                    // dsh web UI（loopback；完整运行时随应用内置后仍是此路径）
                    opts.Url = _webUrl;
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
                    "io.github.ZK-Andy.dotnet-deepseek-harness-desktop", _isDev);
                if (File.Exists(_iconPath)) // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                {
                    opts.IconPath = _iconPath;
                }
                else
                {
                    Services.HostLog.Write($"[host] icon 缺失：{_iconPath}");
                }

                Services.HostLog.Write($"[host] Ryn opts: Url={(_webUrl is not null ? _webUrl.ToString() : "null")} ApplicationId={opts.ApplicationId} Icon={(File.Exists(_iconPath) ? _iconPath : "missing")}"); // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                // WebView 调试器默认关闭（正式打包无调试窗口）；开发期设 DSH_DEVTOOLS=1 开启。
                opts.DevTools = Environment.GetEnvironmentVariable("DSH_DEVTOOLS") == "1";
            })
            .ConfigureServices(RegisterServices)
            .Build();

        _windowAccessor = _app.Services.GetRequiredService<CurrentWindowAccessor>();

        // token：BuildApp 完成（Ryn 应用/windowAccessor/icon/tray 探测已落字段），供后续阶段按类型承诺串联。
        return default;
    }

    private void RegisterServices(IServiceCollection services)
    {
        services.AddRynCommands();
        // 宿主导航回调（Ryn 0.32.0 Ryn.Callbacks）：在导航边界统一拦截外部链接（ADR ryn-navigation-callbacks）。
        services.AddRynCallbacks();
        services.AddRynNavigationCallbacks();
        // 覆盖源生成的 handler 无参注册：导航回调依赖（openExternal 打开器 / 日志 /
        // 当前页面 origin）在 ConfigureServices 时已知，经工厂注入。
        services.AddSingleton(sp => new Services.RynNavigationCallbacks(
            opener: null,
            log: Services.HostLog.Write,
            currentOrigin: _webUrl?.GetLeftPart(UriPartial.Authority),
            // 外部链接打开失败 → 推事件给页面，companion 渲染 toast（R2 N2）。EmitEvent 走
            // deferred IRynWebView（窗口就绪后转发），在导航回调触发时页面必然已加载。
            notifyLinkFail: url => sp.GetRequiredService<IRynWebView>().EmitEvent(
                "desktop.externalLinkOpenerFailed",
                new Services.ExternalLinkOpenerFailedFrame(url),
                Services.AppJsonContext.Default.ExternalLinkOpenerFailedFrame)));
        // 外部链接 → 系统默认浏览器（宿主命令路由，见 implemented ADR open-external-links-in-system-browser）
        services.AddSingleton<ICommandRouter>(new Services.ExternalLinkCommandRouter(log: Services.HostLog.Write));
        // dsh 语言变更桥接（desktop.companion.setLocale，ADR host-ui-locale）
        services.AddSingleton<ICommandRouter>(new Services.CompanionLocaleCommandRouter(_uiLocale, log: Services.HostLog.Write));
        // 诊断包导出（desktop.diagnostics.export；ryn.json 的 desktop 能力面已放行）
        services.AddSingleton<ICommandRouter>(new Services.DesktopDiagnosticsCommandRouter(
            log: Services.HostLog.Write, healthSnapshot: () => _healthMonitor?.Snapshot));
        // 恢复页退出（desktop.recovery.exit）：先批准关窗闸门再 Close——hide-to-tray 拦截下
        // 未批准的 Close 会吞成隐藏；顺序契约与托盘退出同款（ADR diag-masking-and-recovery-page）
        services.AddSingleton<ICommandRouter>(sp => new Services.RecoveryCommandRouter(
            closeWindow: () => sp.GetRequiredService<IRynWindow>().Close(),
            _closeGate,
            Services.HostLog.Write));
        // 引导重试命令（desktop.bootstrap.retry，ADR online-first-unbundled-runtime）：
        // wwwroot 引导页的重试按钮 → 闸门放行引导循环。gate 实例在 Run 顶部创建，
        // 引导任务与路由共用同一实例
        services.AddSingleton<ICommandRouter>(new Services.BootstrapCommandRouter(
            _bootstrapGate, Services.HostLog.Write));
        // 插件引导决策命令（desktop.preinstall.choose，ADR reference-alignment 批次二）：
        // wwwroot 引导页「插件引导」步的确认装/跳过 → 闸门放行引导任务
        services.AddSingleton<ICommandRouter>(new Services.PreinstallCommandRouter(
            _preinstallGate, Services.HostLog.Write));
        // 开机自启开关（desktop.autostart.getState/set）
        services.AddSingleton<ICommandRouter>(new Services.AutostartCommandRouter(log: Services.HostLog.Write));
        // 关闭最小化到托盘偏好（desktop.closeToTray.getState/set）；available 惰性求值——
        // 服务注册早于托盘初始化，trayReady 由外层闭包稍后赋值
        services.AddSingleton<ICommandRouter>(new Services.Tray.CloseToTrayCommandRouter(
            _closeBehavior, () => _trayReady, log: Services.HostLog.Write));
        // 自更新命令：desktop.update.getState / check / install（dev 门禁下不注册路由，invoke 自然失败）
        if (_updateMachine is not null)
        {
            services.AddSingleton<ICommandRouter>(new Services.Update.DesktopUpdateCommandRouter(_updateMachine, log: Services.HostLog.Write, backgroundToken: () => _supervisorCtsRef?.Token ?? CancellationToken.None));
        }
        RegisterTrayServices(services);
    }

    /// <summary>托盘服务注册（ADR shell-tray-hide-to-tray）：图标+菜单 + 事件路由（窗口动作经委托接 deferred 代理）。</summary>
    private void RegisterTrayServices(IServiceCollection services)
    {
        // 托盘（批次三）：图标+菜单；点击语义经 companion 中继
        // 回 desktop.tray.event 在宿主解析——EmitEvent 是 Ryn 插件内部属性，不在源生成通道
        if (_trayAvailable)
        {
            services.AddRynTray(o =>
            {
                o.IconPath = _iconPath;
                o.Tooltip = "DeepSeek Harness Desktop";
            });
        }
        // 托盘事件路由：窗口动作经委托接 deferred 代理（注册期无需窗口就绪；
        // 委托注入让退出顺序契约可用记序 fake 测试）
        services.AddSingleton<ICommandRouter>(sp =>
        {
            IRynWindow trayWindow = sp.GetRequiredService<IRynWindow>();
            return new Services.Tray.DesktopTrayCommandRouter(
                showWindow: () => RecallAsync(trayWindow),
                closeWindow: () =>
                {
                    // 编排在 supervisorCts 声明后接线，而托盘退出必经托盘菜单的用户交互、
                    // 必然晚于接线，故此处不可能为 null
                    _orderlyQuit!();
                },
                _closeGate,
                _updateMachine,
                Services.HostLog.Write,
                notify: (title, message) =>
                    sp.GetRequiredService<TrayService>().ShowNotification(title, message));
        });
    }

    private void ShowTray(AppToken app)
    {
        // 托盘就绪化（批次三）：装菜单并显示。失败只降级记日志——无托盘环境是合法运行环境；
        // 但下方 hide-to-tray 拦截必须与托盘同 gate：没有召回通道还拦截关窗等于把窗口藏死。
        // 顺序契约：必须先 Show 再 SetMenu——Linux 后端在 Show 前尚未注册 StatusNotifierItem，
        // SetMenu 经 `_item?.` 静默丢弃（v0.3.0 实机图标可见但菜单全无的根因）；macOS 的
        // RebuildMenu 在 status item 未创建时同样丢弃。Windows 两序皆可（菜单右键时才读）。
        if (_trayAvailable)
        {
            try
            {
                TrayService tray = _app.Services.GetRequiredService<TrayService>();
                tray.Show();
                tray.SetMenu(Services.Tray.TrayMenuActions.BuildItems(includeUpdateItem: _updateMachine is not null, _uiLocale));
                _trayReady = true;
                Services.HostLog.Write("[host] 系统托盘已注册");
                // dsh 语言切换 → companion 上报 → locale 变化即重建菜单（ADR host-ui-locale）
                _uiLocale.Changed += () =>
                {
                    try
                    {
                        tray.SetMenu(Services.Tray.TrayMenuActions.BuildItems(includeUpdateItem: _updateMachine is not null, _uiLocale));
                    }
                    catch (Exception ex1)
                    {
                        // 菜单重建失败可容忍：保留旧菜单（文案为上一语言），托盘功能不受损
                        Services.HostLog.Write($"[host] 托盘菜单重建失败（保留旧菜单）：{ex1.Message}");
                    }
                };
            }
            catch (Exception ex)
            {
                Services.HostLog.Write($"[host] 系统托盘初始化失败，关闭窗口将直接退出：{ex.Message}");
            }
        }

        if (_trayReady)
        {
            // IRynWindow 是 deferred 代理：此处窗口尚未创建，Closing 订阅会被缓冲到窗口就绪后挂载。
            // 回调内绝不抛异常——上游对抛异常的 Closing 处理是「放行关窗」，比隐藏更危险。
            IRynWindow trayWindow = _app.Services.GetRequiredService<IRynWindow>();
            trayWindow.Closing += (_, e) =>
            {
                if (!_closeGate.ShouldCancelClose || !_closeBehavior.HideOnClose)
                {
                    // 显式放行通道（托盘退出 / 自更新安装），或用户已选「关闭即退出」
                    return;
                }

                e.Cancel = true;
                _ = HideForTrayAsync(trayWindow);
            };
        }
    }
}
