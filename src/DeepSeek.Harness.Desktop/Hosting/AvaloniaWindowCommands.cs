using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// <see cref="IWindowCommands"/> 的 Avalonia 实现（ADR 2026-10-23-avalonia-caption-bar）：
/// 命令路由在后台线程分发，窗口动作一律封送 UI 线程（Win32 的移动循环会阻塞调用线程，
/// 在池线程上直接发会把 UI 线程晾死）。
/// </summary>
internal sealed class AvaloniaWindowCommands : IWindowCommands
{
    private readonly Window _window;
    private readonly IWindowDrag _drag;
    private readonly Action<string> _log;

    /// <summary>创建动作面。</summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="drag">平台拖拽端口。</param>
    /// <param name="log">日志委托。</param>
    public AvaloniaWindowCommands(Window window, IWindowDrag drag, Action<string> log)
    {
        _window = window;
        _drag = drag;
        _log = log;
    }

    /// <inheritdoc />
    public void BeginMove(int screenX, int screenY) => Run("move", () =>
    {
        if (!_drag.TryBeginMove(_window, ToDevicePixels(screenX, screenY)))
        {
            _log($"[host] 拖拽未发起（端口 {_drag.Name}）");
        }
    });

    /// <inheritdoc />
    public void BeginResize(WindowEdgeKind edge, int screenX, int screenY) => Run($"resize-{EdgeLabel(edge)}", () =>
    {
        // 最大化态无可缩放边（页面通道已短路边缘判定，此处是脱同步期兜底）：直接吞掉，
        // 不给窗口系统发无效缩放（0.7.4 真机实证最大化态 6px 带发 resize）。
        if (_window.WindowState == WindowState.Maximized)
        {
            _log("[host] 缩放忽略（最大化态）");
            return;
        }

        if (!_drag.TryBeginResize(_window, edge, ToDevicePixels(screenX, screenY)))
        {
            _log($"[host] 缩放未发起（端口 {_drag.Name}，边 {EdgeLabel(edge)}）");
        }
    });

    /// <inheritdoc />
    public void ToggleMaximize() => Run("toggle-maximize", () =>
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized);

    /// <inheritdoc />
    public void Minimize() => Run("minimize", () => _window.WindowState = WindowState.Minimized);

    /// <inheritdoc />
    public void Close() => Run("close", _window.Close);

    /// <inheritdoc />
    public void SetFullscreen(bool fullscreen) => Run(fullscreen ? "fullscreen-on" : "fullscreen-off", () =>
        _window.WindowState = fullscreen ? WindowState.FullScreen : WindowState.Normal);

    /// <summary>缩放边的日志名：与页面协议的 kebab-case 同名（页面发 <c>top-left</c>，日志不再漂成
    /// 枚举 <c>TopLeft</c>——命令帧到命令面的判定位靠日志与协议名对读）。</summary>
    /// <param name="edge">缩放边。</param>
    /// <returns>协议侧边名。</returns>
    private static string EdgeLabel(WindowEdgeKind edge) => edge switch
    {
        WindowEdgeKind.Left => "left",
        WindowEdgeKind.Right => "right",
        WindowEdgeKind.Top => "top",
        WindowEdgeKind.Bottom => "bottom",
        WindowEdgeKind.TopLeft => "top-left",
        WindowEdgeKind.TopRight => "top-right",
        WindowEdgeKind.BottomLeft => "bottom-left",
        WindowEdgeKind.BottomRight => "bottom-right",
        _ => edge.ToString(),
    };

    /// <summary>页面给的是 CSS 像素（<c>window.screenX + clientX</c>），X11 的根坐标是设备像素：
    /// 按窗口缩放比换算，否则 scale≠1 时移动/缩放的锚点会按比例偏。</summary>
    /// <param name="screenX">页面坐标 X（CSS 像素）。</param>
    /// <param name="screenY">页面坐标 Y（CSS 像素）。</param>
    /// <returns>设备像素坐标。</returns>
    private PixelPoint ToDevicePixels(int screenX, int screenY)
    {
        double scale = _window.RenderScaling;
        return new PixelPoint((int)Math.Round(screenX * scale), (int)Math.Round(screenY * scale));
    }

    /// <summary>封送 UI 线程执行（已在 UI 线程则直接执行）。命令名随行落日志：页面命令帧是否到达
    /// 命令面是窗口态缺陷群的判定位（到了=输入路由通、问题在后续；没到=页面输入未进桥）。</summary>
    /// <param name="command">命令名（诊断用）。</param>
    /// <param name="action">窗口动作。</param>
    private void Run(string command, Action action)
    {
        _log($"[host] 窗口命令 {command}");
        if (Dispatcher.UIThread.CheckAccess())
        {
            Execute(action);
            return;
        }

        Dispatcher.UIThread.Post(() => Execute(action));
    }

    /// <summary>执行一条窗口动作并兜住异常：命令已回成功帧，UI 线程上的抛出不得掀掉进程。</summary>
    /// <param name="action">窗口动作。</param>
    private void Execute(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log($"[host] 窗口命令执行失败：{ex.Message}");
        }
    }
}
