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
    public void BeginMove(int screenX, int screenY) => Run(() =>
    {
        if (!_drag.TryBeginMove(_window, ToDevicePixels(screenX, screenY)))
        {
            _log($"[host] 拖拽未发起（端口 {_drag.Name}）");
        }
    });

    /// <inheritdoc />
    public void BeginResize(WindowEdgeKind edge, int screenX, int screenY) => Run(() =>
    {
        if (!_drag.TryBeginResize(_window, edge, ToDevicePixels(screenX, screenY)))
        {
            _log($"[host] 缩放未发起（端口 {_drag.Name}，边 {edge}）");
        }
    });

    /// <inheritdoc />
    public void ToggleMaximize() => Run(() =>
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized);

    /// <inheritdoc />
    public void Minimize() => Run(() => _window.WindowState = WindowState.Minimized);

    /// <inheritdoc />
    public void Close() => Run(_window.Close);

    /// <inheritdoc />
    public void SetFullscreen(bool fullscreen) => Run(() =>
        _window.WindowState = fullscreen ? WindowState.FullScreen : WindowState.Normal);

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

    /// <summary>封送 UI 线程执行（已在 UI 线程则直接执行）。</summary>
    /// <param name="action">窗口动作。</param>
    private void Run(Action action)
    {
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
