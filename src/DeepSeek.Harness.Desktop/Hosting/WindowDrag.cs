using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// 无边框窗口的拖拽/缩放平台端口（ADR 2026-10-23-avalonia-caption-bar）：页面区域的指针事件被原生
/// WebView 子窗消费，Avalonia 的 <c>Window.BeginMoveDrag(PointerPressedEventArgs)</c> 拿不到按下事件，
/// 故只能按窗口系统各自的原生通道发起移动/缩放——Win32 命中测试、X11 <c>_NET_WM_MOVERESIZE</c>、
/// macOS 走 <c>Avalonia.Native</c> 的「WebView 消费按下」回退分支。
/// Wayland 的 <c>xdg_toplevel.move</c> 需要触发事件的 seat serial，公开 API 不可达，故该后端不提供端口
/// （窗口保留原生装饰，不注入自绘顶栏）。
/// </summary>
internal interface IWindowDrag
{
    /// <summary>平台端口名（诊断用，如 <c>win32</c>/<c>x11</c>/<c>macos</c>/<c>none</c>）。</summary>
    string Name { get; }

    /// <summary>发起窗口拖拽。</summary>
    /// <param name="window">目标窗口（须已显示且有平台句柄）。</param>
    /// <param name="screenPoint">按下点的屏幕坐标。</param>
    /// <returns>发起成功为 true（之后由窗口系统接管）。</returns>
    bool TryBeginMove(Window window, PixelPoint screenPoint);

    /// <summary>发起窗口缩放。</summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="edge">缩放边。</param>
    /// <param name="screenPoint">按下点的屏幕坐标。</param>
    /// <returns>发起成功为 true。</returns>
    bool TryBeginResize(Window window, WindowEdgeKind edge, PixelPoint screenPoint);
}

/// <summary>平台端口选择（判据按窗口句柄描述符，绝不在 Wayland 上猜）：句柄缺失即无端口。</summary>
internal static class WindowDrag
{
    /// <summary>按当前窗口的平台句柄选择拖拽端口。</summary>
    /// <param name="window">目标窗口（调用点必在句柄就绪之后）。</param>
    /// <param name="log">日志委托。</param>
    /// <returns>端口实例；无可用通道时为不可用端口（动作回 false，由调用方留痕）。</returns>
    public static IWindowDrag For(TopLevel window, Action<string> log)
    {
        var handle = IPlatformHandleAccess.From(window);
        if (handle.Descriptor is null)
        {
            log("[host] 无平台句柄：拖拽/缩放端口不可用");
            return UnsupportedWindowDrag.Instance;
        }

        if (string.Equals(handle.Descriptor, "HWND", StringComparison.Ordinal))
        {
            return Win32WindowDrag.Instance;
        }

        if (string.Equals(handle.Descriptor, "XID", StringComparison.Ordinal))
        {
            return X11WindowDrag.Instance;
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacWindowDrag.Instance;
        }

        log($"[host] 平台句柄 {handle.Descriptor} 无拖拽通道（Wayland 保留原生装饰）");
        return UnsupportedWindowDrag.Instance;
    }

    /// <summary>句柄读取的薄适配（<see cref="TopLevel.TryGetPlatformHandle"/> 的返回值在装配期可能为 null）。</summary>
    private readonly struct IPlatformHandleAccess
    {
        private IPlatformHandleAccess(string? descriptor) => Descriptor = descriptor;

        /// <summary>句柄描述符（<c>HWND</c>/<c>XID</c>/<c>NSWindow</c>…）。</summary>
        public string? Descriptor { get; }

        /// <summary>读窗口句柄描述符。</summary>
        /// <param name="window">目标窗口。</param>
        /// <returns>适配实例。</returns>
        public static IPlatformHandleAccess From(TopLevel window) =>
            new(window.TryGetPlatformHandle()?.HandleDescriptor);
    }
}

/// <summary>无可用通道（Wayland / 句柄缺失）：动作一律回 false，由调用方留痕。</summary>
internal sealed class UnsupportedWindowDrag : IWindowDrag
{
    /// <summary>共享实例（无状态）。</summary>
    public static UnsupportedWindowDrag Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "none";

    /// <inheritdoc />
    public bool TryBeginMove(Window window, PixelPoint screenPoint) => false;

    /// <inheritdoc />
    public bool TryBeginResize(Window window, WindowEdgeKind edge, PixelPoint screenPoint) => false;
}

/// <summary>Win32：<c>ReleaseCapture</c> + <c>WM_NCLBUTTONDOWN</c> 命中测试（与原生标题栏同一条路）。</summary>
internal sealed class Win32WindowDrag : IWindowDrag
{
    private const uint WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;

    /// <summary>共享实例（无状态）。</summary>
    public static Win32WindowDrag Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "win32";

    /// <inheritdoc />
    public bool TryBeginMove(Window window, PixelPoint screenPoint) => Press(window, HtCaption);

    /// <inheritdoc />
    public bool TryBeginResize(Window window, WindowEdgeKind edge, PixelPoint screenPoint) =>
        Press(window, edge switch
        {
            WindowEdgeKind.Left => 10,  // HTLEFT
            WindowEdgeKind.Right => 11, // HTRIGHT
            WindowEdgeKind.Top => 12,   // HTTOP
            WindowEdgeKind.TopLeft => 13,
            WindowEdgeKind.TopRight => 14,
            WindowEdgeKind.Bottom => 15,
            WindowEdgeKind.BottomLeft => 16,
            WindowEdgeKind.BottomRight => 17,
            _ => 0,
        });

    /// <summary>发非客户区按下消息（阻塞到窗口系统结束移动循环；调用点须在 UI 线程）。</summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="hitTest">命中测试码。</param>
    /// <returns>句柄可用即 true。</returns>
    private static bool Press(Window window, int hitTest)
    {
        IntPtr handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        _ = ReleaseCapture();
        _ = SendMessage(handle, WmNcLButtonDown, new IntPtr(hitTest), IntPtr.Zero);
        return true;
    }

    [DllImport("user32", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// X11：给根窗口发 <c>_NET_WM_MOVERESIZE</c>（与 Avalonia 自己的 <c>BeginMoveResize</c> 同一条协议）。
/// 用独立连接打开 DISPLAY——消息只经窗口管理器路由，不需要与 Avalonia 共用连接。
/// </summary>
internal sealed class X11WindowDrag : IWindowDrag
{
    private const string LibX11 = "libX11.so.6";
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const int EventSize = 192;

    // _NET_WM_MOVERESIZE direction 值（EWMH）
    private const int DirectionMove = 8;
    private const int DirectionTopLeft = 0;
    private const int DirectionTop = 1;
    private const int DirectionTopRight = 2;
    private const int DirectionRight = 3;
    private const int DirectionBottomRight = 4;
    private const int DirectionBottom = 5;
    private const int DirectionBottomLeft = 6;
    private const int DirectionLeft = 7;

    /// <summary>共享实例（无状态）。</summary>
    public static X11WindowDrag Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "x11";

    /// <inheritdoc />
    public bool TryBeginMove(Window window, PixelPoint screenPoint) =>
        Send(window, DirectionMove, screenPoint);

    /// <inheritdoc />
    public bool TryBeginResize(Window window, WindowEdgeKind edge, PixelPoint screenPoint) =>
        Send(window, edge switch
        {
            WindowEdgeKind.TopLeft => DirectionTopLeft,
            WindowEdgeKind.Top => DirectionTop,
            WindowEdgeKind.TopRight => DirectionTopRight,
            WindowEdgeKind.Right => DirectionRight,
            WindowEdgeKind.BottomRight => DirectionBottomRight,
            WindowEdgeKind.Bottom => DirectionBottom,
            WindowEdgeKind.BottomLeft => DirectionBottomLeft,
            WindowEdgeKind.Left => DirectionLeft,
            _ => -1,
        }, screenPoint);

    /// <summary>发 ClientMessage 到根窗口；指针被页面按住时即由 WM 接管移动/缩放。</summary>
    /// <param name="window">目标窗口（取 XID）。</param>
    /// <param name="direction"><c>_NET_WM_MOVERESIZE</c> 方向值。</param>
    /// <param name="point">按下点屏幕坐标。</param>
    /// <returns>消息已发出为 true；libX11 缺失/句柄为空为 false。</returns>
    private static bool Send(Window window, int direction, PixelPoint point)
    {
        if (direction < 0)
        {
            return false;
        }

        IntPtr windowHandle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        IntPtr display;
        try
        {
            display = XOpenDisplay(null);
        }
        catch (DllNotFoundException)
        {
            return false;
        }

        if (display == IntPtr.Zero)
        {
            return false;
        }

        IntPtr xevent = Marshal.AllocHGlobal(EventSize);
        try
        {
            for (int offset = 0; offset < EventSize; offset += sizeof(long))
            {
                Marshal.WriteInt64(xevent, offset, 0);
            }

            // XClientMessageEvent（LP64 布局）：type@0, send_event@16, display@24, window@32,
            // message_type@40, format@48, data.l@56..96。
            Marshal.WriteInt32(xevent, 0, ClientMessage);
            Marshal.WriteInt32(xevent, 16, 1);
            Marshal.WriteInt64(xevent, 24, display.ToInt64());
            Marshal.WriteInt64(xevent, 32, windowHandle.ToInt64());
            ulong atom = XInternAtom(display, "_NET_WM_MOVERESIZE", false);
            if (atom == 0)
            {
                return false;
            }

            Marshal.WriteInt64(xevent, 40, unchecked((long)atom));
            Marshal.WriteInt32(xevent, 48, 32);
            Marshal.WriteInt64(xevent, 56, point.X);
            Marshal.WriteInt64(xevent, 64, point.Y);
            Marshal.WriteInt64(xevent, 72, direction);
            Marshal.WriteInt64(xevent, 80, 1); // 左键
            Marshal.WriteInt64(xevent, 88, 1); // 应用发起

            // 注意：连接独立，这里的 XUngrabPointer 解不掉 WebView 所在客户端的隐式抓取；
            // 该前置由真机验收覆盖（部分 WM 会回 AlreadyGrabbed 忽略本次移动）。
            _ = XUngrabPointer(display, IntPtr.Zero);
            int sent = XSendEvent(
                display,
                XDefaultRootWindow(display),
                false,
                SubstructureRedirectMask | SubstructureNotifyMask,
                xevent);
            _ = XFlush(display);
            return sent != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(xevent);
            _ = XCloseDisplay(display);
        }
    }

    [DllImport(LibX11, EntryPoint = "XOpenDisplay", ExactSpelling = true)]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(LibX11, EntryPoint = "XDefaultRootWindow", ExactSpelling = true)]
    private static extern ulong XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11, EntryPoint = "XInternAtom", ExactSpelling = true)]
    private static extern ulong XInternAtom(IntPtr display, string atomName, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport(LibX11, EntryPoint = "XSendEvent", ExactSpelling = true)]
    private static extern int XSendEvent(IntPtr display, ulong window, [MarshalAs(UnmanagedType.Bool)] bool propagate, long eventMask, IntPtr xevent);

    [DllImport(LibX11, EntryPoint = "XFlush", ExactSpelling = true)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibX11, EntryPoint = "XUngrabPointer", ExactSpelling = true)]
    private static extern int XUngrabPointer(IntPtr display, IntPtr time);

    [DllImport(LibX11, EntryPoint = "XCloseDisplay", ExactSpelling = true)]
    private static extern int XCloseDisplay(IntPtr display);
}

/// <summary>
/// macOS：<c>Avalonia.Native</c> 的 <c>BeginMoveDrag()</c> 不消费事件参数，且其原生实现显式回退到
/// <c>[NSApp currentEvent]</c>——正是「按下被 WebView 消费」的场景，故直接调用即可。
/// 缩放不代劳：macOS 的边框缩放由窗口系统按 <see cref="Avalonia.Controls.WindowDecorations.BorderOnly"/> 提供。
/// </summary>
internal sealed class MacWindowDrag : IWindowDrag
{
    /// <summary>共享实例（无状态）。</summary>
    public static MacWindowDrag Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "macos";

    /// <inheritdoc />
    public bool TryBeginMove(Window window, PixelPoint screenPoint)
    {
        window.BeginMoveDrag(null!);
        return true;
    }

    /// <inheritdoc />
    public bool TryBeginResize(Window window, WindowEdgeKind edge, PixelPoint screenPoint) => false;
}
