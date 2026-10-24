namespace DeepSeek.Harness.Desktop.Core.Ipc;

/// <summary>
/// 宿主↔页面命令 ID 单一事实源（ADR host-command-ids；R3/IPC 强类型）：壳侧命令路由、伴生插件
/// （<c>plugins/dsh-desktop-companion</c>）与引导页共享的命令名在此定案。
/// </summary>
/// <remarks>
/// 协议常量，不随部署环境变化，故为编译期常量（可进 <c>is</c>/<c>case</c> 模式匹配）。
/// 改一个命令名只改这里一处；壳侧与插件侧不同步的改名由
/// <c>scripts/verify-host-command-ids.py</c> 判红（不改则两侧字面量失配）。
/// 事件名（宿主推给页面）与命令名（页面调宿主）同列——两者都走同一条 IPC 通道，漂移的后果一样。
/// </remarks>
public static class HostCommandIds
{
    /// <summary>自更新：取当前更新状态。</summary>
    public const string UpdateGetState = "desktop.update.getState";

    /// <summary>自更新：检查新版本（后台执行，响应先回当前态）。</summary>
    public const string UpdateCheck = "desktop.update.check";

    /// <summary>自更新：安装已就绪的更新包。</summary>
    public const string UpdateInstall = "desktop.update.install";

    /// <summary>开机自启：取开关状态。</summary>
    public const string AutostartGetState = "desktop.autostart.getState";

    /// <summary>开机自启：设开关。</summary>
    public const string AutostartSet = "desktop.autostart.set";

    /// <summary>关闭到托盘：取开关状态。</summary>
    public const string CloseToTrayGetState = "desktop.closeToTray.getState";

    /// <summary>关闭到托盘：设开关。</summary>
    public const string CloseToTraySet = "desktop.closeToTray.set";

    /// <summary>应用重启（受理即回帧，重启有冲刷延迟）。</summary>
    public const string AppRestart = "desktop.app.restart";

    /// <summary>伴生插件：页面降级报障。</summary>
    public const string CompanionReport = "desktop.companion.report";

    /// <summary>伴生插件：上报 dsh 语言（宿主据此改 UI 语言）。</summary>
    public const string CompanionSetLocale = "desktop.companion.setLocale";

    /// <summary>引导页：拉取宿主当前 UI 语言。</summary>
    public const string UiGetLocale = "desktop.ui.getLocale";

    /// <summary>诊断包导出（白名单 zip，返回落盘路径）。</summary>
    public const string DiagnosticsExport = "desktop.diagnostics.export";

    /// <summary>首启引导：重试（引导失败后由页面触发）。</summary>
    public const string BootstrapRetry = "desktop.bootstrap.retry";

    /// <summary>恢复页：退出应用（先批准关窗闸门再关窗）。</summary>
    public const string RecoveryExit = "desktop.recovery.exit";

    /// <summary>外部链接打开失败（宿主→页面事件，companion 渲染 toast）。</summary>
    public const string ExternalLinkOpenerFailed = "desktop.externalLinkOpenerFailed";

    /// <summary>窗口：开始拖拽（无边框顶栏；页面给屏幕坐标）。</summary>
    public const string WindowStartDrag = "window.startDrag";

    /// <summary>窗口：开始缩放（无边框窗口的边缘判定在页面侧；页面给边与屏幕坐标）。</summary>
    public const string WindowStartResize = "window.startResize";

    /// <summary>窗口：最大化/还原切换。</summary>
    public const string WindowToggleMaximize = "window.toggleMaximize";

    /// <summary>窗口：最小化。</summary>
    public const string WindowMinimize = "window.minimize";

    /// <summary>窗口：关闭（走关窗闸门）。</summary>
    public const string WindowClose = "window.close";

    /// <summary>窗口：进入/退出原生全屏（macOS 绿灯语义）。</summary>
    public const string WindowSetFullscreen = "window.setFullscreen";

    /// <summary>外部链接打开（导航层与页面脚本的通用入口；非 <c>desktop.</c> 前缀是上游约定）。</summary>
    public const string OpenExternal = "app.openExternal";
}
