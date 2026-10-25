using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// X11 首映射焦点兜底（ADR 2026-10-25-exit-kill-focus-debounce-batch）：装后自动重启上下文实测
/// 首映射失焦——<c>_NET_ACTIVE_WINDOW</c> 悬在无 WM_CLASS 幽灵窗上，键盘面（媒体键/页内 tab/Alt-Tab）
/// 全废（键盘事件只投焦点窗）。修法：首映射后向根窗口发 <c>_NET_ACTIVE_WINDOW</c> 激活请求
/// （source indication=1，应用发起；与 <see cref="X11WindowDrag"/> 同型的根窗口 ClientMessage），
/// 并做<b>有界复验</b>——短窗内活动窗仍非本窗则补发（Mutter 的焦点窃取防护可能吞首次请求），
/// 复验窗外不再干预（此后用户自行选择焦点是合法态）。
/// <para><b>WM_TAKE_FOCUS 路线不可行</b>（源码级证实，计划书原第 1/2 件改道）：Avalonia 写的
/// <c>WM_PROTOCOLS</c> 不含该原子、其 ClientMessage 处理也忽略之；Mutter 补发该消息用
/// <c>event_mask=0</c>——X 协议下只投递建窗客户端（Avalonia 连接），本壳的独立连接永远收不到；
/// 且只声明不响应会让 Mutter 放弃直落焦点改等客户端应答，反而恶化。</para>
/// </summary>
internal static class X11FocusBootstrap
{
    private const string LibX11 = "libX11.so.6";
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const int EventSize = 192;
    private const long AnyPropertyType = 0;

    /// <summary>复验时点（毫秒）：覆盖焦点窃取防护的短窗；总时长有界，不与用户的后续焦点选择竞争。</summary>
    private static readonly int[] s_recheckDelaysMs = [300, 800, 1500];

    private static int s_started;

    /// <summary>首映射后发起激活请求与有界复验（best-effort）：非 Linux、句柄非 XID、libX11 不可用
    /// 都静默跳过；进程级 once——托盘唤回重触发 <c>OnOpened</c> 时不得重跑（唤回路径由
    /// <c>Show+Activate</c> 的宿主既有激活通道覆盖）。</summary>
    /// <param name="window">目标窗口（须已显示且有 XID 句柄）。</param>
    /// <param name="log">日志回调。</param>
    public static void Apply(TopLevel window, Action<string> log)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IntPtr handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || window.TryGetPlatformHandle()?.HandleDescriptor != "XID")
        {
            return;
        }

        if (Interlocked.Exchange(ref s_started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() => Run(handle, log));
    }

    /// <summary>发激活请求并按 <see cref="s_recheckDelaysMs"/> 复验：活动窗不是本窗就补发；任一步失败
    /// 留痕即收（焦点缺失可由托盘唤回自愈，不值得打断）。</summary>
    private static void Run(IntPtr windowHandle, Action<string> log)
    {
        IntPtr display;
        try
        {
            display = XOpenDisplay(null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // 所吞=libX11 装载形态异常（缺失/坏镜像/入口缺失）：焦点兜底是 best-effort，
            // 无 libX11 即无 X11 通道可言，静默放弃（与 X11WindowIdentity 同判据）
            return;
        }

        if (display == IntPtr.Zero)
        {
            return;
        }

        try
        {
            IntPtr root = XDefaultRootWindow(display);
            IntPtr activeWindowAtom = XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
            if (root == IntPtr.Zero || activeWindowAtom == IntPtr.Zero)
            {
                return;
            }

            SendActivate(display, root, windowHandle, activeWindowAtom);
            log("[host] 焦点兜底：已发 _NET_ACTIVE_WINDOW 激活请求");

            foreach (int delayMs in s_recheckDelaysMs)
            {
                Thread.Sleep(delayMs);
                if (ReadActiveWindow(display, root, activeWindowAtom) == windowHandle)
                {
                    return;
                }

                SendActivate(display, root, windowHandle, activeWindowAtom);
                log($"[host] 焦点兜底：活动窗仍非本窗，{delayMs}ms 补发激活请求");
            }
        }
        catch (Exception ex)
        {
            // 所吞=X11 期库级异常（连接中断等）：焦点缺失可由托盘唤回自愈，留痕即可
            log($"[host] 焦点兜底异常（仅影响首映射聚焦）：{ex.Message}");
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    /// <summary>向根窗口发 <c>_NET_ACTIVE_WINDOW</c> ClientMessage（EWMH）：window=本窗、
    /// l[0]=source indication（1=应用发起）、l[1]=timestamp（0=CurrentTime）、l[2]=当前活动窗（0）。</summary>
    private static void SendActivate(IntPtr display, IntPtr root, IntPtr windowHandle, IntPtr messageAtom)
    {
        IntPtr xevent = Marshal.AllocHGlobal(EventSize);
        try
        {
            for (int offset = 0; offset < EventSize; offset += sizeof(long))
            {
                Marshal.WriteInt64(xevent, offset, 0);
            }

            // XClientMessageEvent（LP64 布局，同 X11WindowDrag）：type@0, send_event@16, display@24,
            // window@32, message_type@40, format@48, data.l@56..96。
            Marshal.WriteInt32(xevent, 0, ClientMessage);
            Marshal.WriteInt32(xevent, 16, 1);
            Marshal.WriteInt64(xevent, 24, display.ToInt64());
            Marshal.WriteInt64(xevent, 32, windowHandle.ToInt64());
            Marshal.WriteInt64(xevent, 40, messageAtom.ToInt64());
            Marshal.WriteInt32(xevent, 48, 32);
            Marshal.WriteInt64(xevent, 56, 1); // source indication：应用发起
            Marshal.WriteInt64(xevent, 64, 0); // timestamp：CurrentTime
            Marshal.WriteInt64(xevent, 72, 0); // 当前活动窗：无

            _ = XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, xevent);
            _ = XFlush(display);
        }
        finally
        {
            Marshal.FreeHGlobal(xevent);
        }
    }

    /// <summary>读根窗口的 <c>_NET_ACTIVE_WINDOW</c>（ATOM 型 32 位属性，取首个元素）；读不到回 IntPtr.Zero。</summary>
    private static IntPtr ReadActiveWindow(IntPtr display, IntPtr root, IntPtr activeWindowAtom)
    {
        if (XGetWindowProperty(
                display, root, activeWindowAtom, 0, 1, false, AnyPropertyType,
                out IntPtr actualType, out int actualFormat, out IntPtr nitems,
                out IntPtr bytesAfter, out IntPtr prop) != 0)
        {
            return IntPtr.Zero;
        }

        try
        {
            if (nitems.ToInt64() < 1 || actualFormat != 32 || prop == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            return new IntPtr(Marshal.ReadInt64(prop));
        }
        finally
        {
            if (prop != IntPtr.Zero)
            {
                _ = XFree(prop);
            }
        }
    }

    [DllImport(LibX11, EntryPoint = "XOpenDisplay", ExactSpelling = true)]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(LibX11, EntryPoint = "XDefaultRootWindow", ExactSpelling = true)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11, EntryPoint = "XInternAtom", ExactSpelling = true)]
    private static extern IntPtr XInternAtom(IntPtr display, string atomName, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport(LibX11, EntryPoint = "XSendEvent", ExactSpelling = true)]
    private static extern int XSendEvent(IntPtr display, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool propagate, long eventMask, IntPtr xevent);

    [DllImport(LibX11, EntryPoint = "XFlush", ExactSpelling = true)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibX11, EntryPoint = "XCloseDisplay", ExactSpelling = true)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11, EntryPoint = "XGetWindowProperty", ExactSpelling = true)]
    private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, long offset, long length, [MarshalAs(UnmanagedType.Bool)] bool delete, long reqType,
        out IntPtr actualType, out int actualFormat, out IntPtr nitems, out IntPtr bytesAfter, out IntPtr prop);

    [DllImport(LibX11, EntryPoint = "XFree", ExactSpelling = true)]
    private static extern int XFree(IntPtr data);
}
