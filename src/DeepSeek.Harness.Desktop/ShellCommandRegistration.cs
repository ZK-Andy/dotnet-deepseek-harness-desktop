using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop;

/// <summary>壳级命令路由注册（组合根注册下沉为按域 AddXxx，ADR compose-root-form-separation）：
/// 外部链接导航、语言桥、开机自启、页面降级报障四域。TODO(rehome-shell-routers)：路由类型仍居根命名空间，
/// 按域分文件归位时随迁并删除本行。</summary>
internal static class ShellCommandRegistration
{
    /// <summary>外部链接打开（宿主命令路由，ADR open-external-links-in-system-browser）与导航回调依赖覆盖：
    /// 宿主导航回调（Ryn 0.32.0 Ryn.Callbacks）在导航边界统一拦截外部链接——当前页面 origin 即代理源
    /// （页面永驻代理内；dsh 自指 3xx 由代理内部跟完，外链照走系统浏览器）。覆盖源生成的 handler 无参注册：
    /// 导航回调依赖（openExternal 打开器 / 日志 / 当前页面 origin）在 ConfigureServices 时已知，经工厂注入。</summary>
    public static IServiceCollection AddExternalLinkRouting(this IServiceCollection services, DshLoopbackProxy? proxy)
    {
        services.AddSingleton(sp => new RynNavigationCallbacks(
            opener: null,
            log: HostLog.Write,
            currentOrigin: proxy?.Origin,
            // 外部链接打开失败 → 推事件给页面，companion 渲染 toast（R2 N2）。EmitEvent 走
            // deferred IRynWebView（窗口就绪后转发），在导航回调触发时页面必然已加载。
            notifyLinkFail: url => sp.GetRequiredService<IRynWebView>().EmitEvent(
                "desktop.externalLinkOpenerFailed",
                new ExternalLinkOpenerFailedFrame(url),
                AppJsonContext.Default.ExternalLinkOpenerFailedFrame)));
        // 外部链接 → 系统默认浏览器（宿主命令路由，见 implemented ADR open-external-links-in-system-browser）
        services.AddSingleton<ICommandRouter>(new ExternalLinkCommandRouter(log: HostLog.Write));
        return services;
    }

    /// <summary>dsh 语言变更桥接（desktop.companion.setLocale，ADR host-ui-locale）与引导页语言拉取
    /// （desktop.ui.getLocale）：页面在 dsh 启动前渲染，语言只能由宿主给。</summary>
    public static IServiceCollection AddLocaleCommands(this IServiceCollection services, UiLocale uiLocale)
    {
        services.AddSingleton<ICommandRouter>(new CompanionLocaleCommandRouter(uiLocale, log: HostLog.Write));
        services.AddSingleton<ICommandRouter>(new UiLocaleCommandRouter(uiLocale));
        return services;
    }

    /// <summary>伴生插件页面降级报障（desktop.companion.report，ADR companion-switch-degradation-recovery）：
    /// 页面侧失败经此进 host.log——通道本身失败时页面先积压，待通道恢复补报。</summary>
    public static IServiceCollection AddCompanionReportCommand(this IServiceCollection services)
    {
        services.AddSingleton<ICommandRouter>(new CompanionReportCommandRouter(log: HostLog.Write));
        return services;
    }

    /// <summary>开机自启开关（desktop.autostart.getState/set）。</summary>
    public static IServiceCollection AddAutostartCommands(this IServiceCollection services)
    {
        services.AddSingleton<ICommandRouter>(new AutostartCommandRouter(log: HostLog.Write));
        return services;
    }

    /// <summary>应用重启（desktop.app.restart，ADR app-restart-native-switch）：受理即回帧、
    /// 延迟冲刷窗口后先批准关窗闸门再走退出管道重启。管道与托盘控制器在编排期回填，
    /// 命令必经页面交互触发，调用时点必已就绪（与 TrayRegistration 的接线注释同理）。</summary>
    public static IServiceCollection AddAppRestartCommand(this IServiceCollection services, StartupWiring wiring)
    {
        services.AddSingleton<ICommandRouter>(new AppRestartCommandRouter(
            closeGate: wiring.Tray!.CloseGate,
            restart: () => wiring.Exit!.Restart(AppRelaunch.SpawnSelf),
            log: HostLog.Write));
        return services;
    }
}
