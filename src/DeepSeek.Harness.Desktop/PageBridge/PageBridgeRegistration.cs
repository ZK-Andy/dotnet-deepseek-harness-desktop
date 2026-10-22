using System.Text.Json;
using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>页面桥域命令路由注册（装配面注册下沉为按域 AddXxx，ADR compose-root-form-separation）：
/// 外部链接导航、语言桥、开机自启、页面降级报障四域（ADR shell-router-rehoming）。</summary>
internal static class PageBridgeRegistration
{
    /// <summary>外部链接打开（宿主命令路由，ADR open-external-links-in-system-browser）与导航回调依赖覆盖：
    /// 宿主导航回调（Ryn 0.32.0 Ryn.Callbacks）在导航边界统一拦截外部链接——当前页面 origin 即代理源
    /// （页面永驻代理内；dsh 自指 3xx 由代理内部跟完，外链照走系统浏览器）。覆盖源生成的 handler 无参注册：
    /// 导航回调依赖（系统浏览器端口 / 日志 / 当前页面 origin）在 ConfigureServices 时已知，经工厂注入。</summary>
    public static IServiceCollection AddExternalLinkRouting(this IServiceCollection services, DshLoopbackProxy? proxy)
    {
        // 平台面在 Infrastructure 实现（ADR host-boundary-ports）；壳侧只认端口。
        services.AddSingleton<ISystemBrowser>(new SystemBrowserService());
        services.AddSingleton(sp => new RynNavigationCallbacks(
            browser: sp.GetRequiredService<ISystemBrowser>(),
            log: HostLog.Write,
            currentOrigin: proxy?.Origin,
            // 外部链接打开失败 → 推事件给页面，companion 渲染 toast（R2 N2）。推送经端口
            // （Ryn 侧为 deferred IRynWebView，Avalonia 侧为 window.__shell），导航回调触发时页面必然已加载。
            notifyLinkFail: url => sp.GetRequiredService<IPageEventPublisher>().EmitEvent(
                HostCommandIds.ExternalLinkOpenerFailed,
                JsonSerializer.Serialize(
                    new ExternalLinkOpenerFailedFrame(url),
                    AppJsonContext.Default.ExternalLinkOpenerFailedFrame))));
        // 外部链接 → 系统默认浏览器（宿主命令路由，见 implemented ADR open-external-links-in-system-browser）
        services.AddSingleton<ICommandRouter>(sp => new ExternalLinkCommandRouter(
            sp.GetRequiredService<ISystemBrowser>(),
            log: HostLog.Write));
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

    /// <summary>新宿主（Avalonia）页面桥装配（ADR avalonia-g2-shell-bridge）：系统浏览器端口、命令路由表与
    /// 桥壳在此构造（R1a 的 new 落注册处，装配面只调用）。</summary>
    /// <param name="shellOrigin">壳 origin（代理 URL 的 authority）；null = 代理未绑定。</param>
    /// <param name="uiLocale">宿主 UI 语言单点。</param>
    /// <param name="macChrome">顶栏是否 macOS 形态。</param>
    /// <param name="captionHeightPx">顶栏高度（CSS 像素）。</param>
    /// <param name="healthInterval">健康探针首拍延迟/间隔。</param>
    /// <param name="log">日志委托。</param>
    /// <returns>页面桥装配壳（窗口挂载后接上）。</returns>
    public static Hosting.ShellBridgeHost CreateShellBridgeHost(
        string? shellOrigin,
        UiLocale uiLocale,
        bool macChrome,
        int captionHeightPx,
        TimeSpan healthInterval,
        Action<string> log)
    {
        var browser = new SystemBrowserService();
        ICommandRouter[] routers =
        {
            new ExternalLinkCommandRouter(browser, log),
            new UiLocaleCommandRouter(uiLocale),
            new CompanionLocaleCommandRouter(uiLocale, log),
            new CompanionReportCommandRouter(log),
        };
        return new Hosting.ShellBridgeHost(routers, shellOrigin, browser, macChrome, captionHeightPx, healthInterval, log);
    }

    /// <summary>旧宿主（Ryn）的页面桥端口适配（ADR avalonia-g2-shell-bridge）：每页脚本注册与事件推送
    /// 在 Ryn 侧各一个适配器；构造收在注册处（根不直接 new 自有类型，C5）。</summary>
    public static IServiceCollection AddRynPageBridgePorts(this IServiceCollection services)
    {
        services.AddSingleton<IPageScriptRegistry>(sp => new RynPageScriptRegistry(() => sp.GetRequiredService<IRynWebView>()));
        services.AddSingleton<IPageEventPublisher>(sp => new RynPageEventPublisher(() => sp.GetRequiredService<IRynWebView>()));
        return services;
    }

    /// <summary>开机自启开关（desktop.autostart.getState/set）。</summary>
    public static IServiceCollection AddAutostartCommands(this IServiceCollection services)
    {
        // 平台面（注册表 / desktop entry / plist）在 Infrastructure 实现（ADR host-boundary-ports）。
        services.AddSingleton<IAutostart>(new AutostartService());
        services.AddSingleton<ICommandRouter>(sp => new AutostartCommandRouter(
            sp.GetRequiredService<IAutostart>(),
            log: HostLog.Write));
        return services;
    }
}
