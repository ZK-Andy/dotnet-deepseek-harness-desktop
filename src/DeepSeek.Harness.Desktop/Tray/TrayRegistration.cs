using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Tray;

/// <summary>托盘域注册（组合根注册下沉为按域 AddXxx，ADR compose-root-form-separation）：
/// Ryn 托盘服务 + 托盘事件路由 + 关窗偏好路由。</summary>
internal static class TrayRegistration
{
    /// <summary>托盘服务注册（ADR shell-tray-hide-to-tray）：图标+菜单 + 事件路由（窗口动作经委托接 deferred 代理）。
    /// 注册时点托盘控制器已在组合根装配（<see cref="StartupWiring.Tray"/> 回填），可用性就此定稿。</summary>
    public static IServiceCollection AddTrayServices(this IServiceCollection services, UpdateSetup update, StartupWiring wiring, UiLocale uiLocale)
    {
        TrayController tray = wiring.Tray!;
        // 托盘（批次三）：图标+菜单；点击语义经 companion 中继
        // 回 desktop.tray.event 在宿主解析——EmitEvent 是 Ryn 插件内部属性，不在源生成通道
        if (tray.IsAvailable)
        {
            services.AddRynTray(o =>
            {
                o.IconPath = tray.IconPath;
                o.Tooltip = "DeepSeek Harness Desktop";
            });
        }

        // 托盘事件路由：窗口动作经委托接 deferred 代理（注册期无需窗口就绪；
        // 委托注入让退出顺序契约可用记序 fake 测试）
        services.AddSingleton<ICommandRouter>(sp => new DesktopTrayCommandRouter(
            showWindow: () => tray.RecallAsync(),
            closeWindow: () =>
            {
                // 管道在 supervisorCts 声明后接线，而托盘退出必经托盘菜单的用户交互、
                // 必然晚于接线，故此处不可能为 null
                wiring.Exit!.OrderlyQuit();
            },
            restart: () =>
            {
                // 同上：托盘重启必经菜单交互，接线时点必已就绪。
                // Restart 内部先回收再 spawn（旧 dsh 树不死透会死于看门狗，ADR app-restart-native-switch）
                wiring.Exit!.Restart(AppRelaunch.SpawnSelf);
            },
            tray.CloseGate,
            update.Updates.Machine,
            uiLocale,
            HostLog.Write,
            notify: (title, message) =>
                sp.GetRequiredService<TrayService>().ShowNotification(title, message)));

        // 关闭最小化到托盘偏好（desktop.closeToTray.getState/set）；available 惰性求值——
        // 服务注册早于托盘初始化，trayReady 由外层闭包稍后赋值
        services.AddSingleton<ICommandRouter>(new CloseToTrayCommandRouter(
            tray.CloseBehavior, () => tray.IsReady, log: HostLog.Write));
        return services;
    }
}
