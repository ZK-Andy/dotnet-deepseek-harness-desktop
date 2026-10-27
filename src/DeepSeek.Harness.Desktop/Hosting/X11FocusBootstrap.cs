using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// X11 首映射焦点兜底（ADR 2026-10-25-exit-kill-focus-debounce-batch、
/// 2026-10-28-first-map-input-focus-recheck）：装后自动重启上下文实测首映射失焦——
/// <c>_NET_ACTIVE_WINDOW</c> 悬在无 WM_CLASS 幽灵窗上，键盘面（媒体键/页内 tab/Alt-Tab）
/// 全废（键盘事件只投焦点窗）。修法：首映射后向根窗口发 <c>_NET_ACTIVE_WINDOW</c> 激活请求
/// （source indication=1，应用发起；与 <see cref="X11WindowDrag"/> 同型的根窗口 ClientMessage），
/// 并做<b>有界复验</b>——短窗内键盘焦点本体（<c>XGetInputFocus</c>）未达本窗则按
/// <see cref="Decide"/> 补激活请求或直落 XSetInputFocus（10-28 实测：复验只读 EWMH 代理指标时，
/// 活动窗已指向本窗而输入焦点未跟随的背离态会被放行），复验窗外不再干预（此后用户自行选择
/// 焦点是合法态）。
/// <para><b>WM_TAKE_FOCUS 路线不可行</b>（源码级证实，计划书原第 1/2 件改道）：Avalonia 写的
/// <c>WM_PROTOCOLS</c> 不含该原子、其 ClientMessage 处理也忽略之；Mutter 补发该消息用
/// <c>event_mask=0</c>——X 协议下只投递建窗客户端（Avalonia 连接），本壳的独立连接永远收不到；
/// 且只声明不响应会让 Mutter 放弃直落焦点改等客户端应答，反而恶化。直落 XSetInputFocus 是另一条
/// 协议路径，不受该投递限制。</para>
/// </summary>
internal static class X11FocusBootstrap
{
    private const string LibX11 = "libX11.so.6";
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const int EventSize = 192;
    private const long AnyPropertyType = 0;
    private const int RevertToPointerRoot = 1;
    private const int MaxAncestryDepth = 16;

    /// <summary>复验时点（毫秒）：覆盖焦点窃取防护的短窗；总时长有界，不与用户的后续焦点选择竞争。</summary>
    private static readonly int[] s_recheckDelaysMs = [300, 800, 1500];

    private static int s_started;

    /// <summary>本连接的错误处理器：返回 0 即吞掉该 X 错误（BadMatch/BadWindow 等），静字段防 GC。</summary>
    private static readonly XErrorHandler s_ignoreXErrors = (_, _) => 0;

    /// <summary>Xlib 错误处理器签名（按连接安装，per-thread）。</summary>
    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    /// <summary>复验动作：键盘焦点本体是否到位（X11 I/O 面之外唯一可单测的决策面）。</summary>
    internal enum FocusRecoveryAction
    {
        /// <summary>input focus 已在本窗，收口。</summary>
        Reached,

        /// <summary>活动窗仍非本窗：补发 EWMH 激活请求（原有路径）。</summary>
        ResendActivate,

        /// <summary>活动窗已指向本窗但 input focus 未跟随（EWMH/本体背离态）：直落 XSetInputFocus。</summary>
        SetInputFocusDirect,
    }

    /// <summary>复验决策：以键盘焦点本体（<c>XGetInputFocus</c>）为准——本体落在本窗或本窗后代即收口，
    /// 不看代理指标；未到位时活动窗非本窗走激活补发、活动窗已指向本窗则直落。</summary>
    /// <param name="self">本窗 XID。</param>
    /// <param name="ewmhActive">根窗口 <c>_NET_ACTIVE_WINDOW</c> 读值（读不到为 0）。</param>
    /// <param name="inputFocus">X input focus 读值（None/PointerRoot 均非本窗）。</param>
    /// <param name="inputFocusInSubtree">input focus 是本窗后代（宿主嵌入的 WebView 子窗等，取焦是
    /// 用户合法态，直落顶窗反而从页面抢焦点）；<paramref name="inputFocus"/> 为本窗时无需此参。</param>
    internal static FocusRecoveryAction Decide(IntPtr self, IntPtr ewmhActive, IntPtr inputFocus, bool inputFocusInSubtree = false)
    {
        if (inputFocus == self || inputFocusInSubtree)
        {
            return FocusRecoveryAction.Reached;
        }

        return ewmhActive == self ? FocusRecoveryAction.SetInputFocusDirect : FocusRecoveryAction.ResendActivate;
    }

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
            // 本连接独占的线程级错误处理器：直落 XSetInputFocus 与父链上行的 XQueryTree 可能抛
            // BadMatch/BadWindow（复验窗内用户最小化/窗口销毁等），默认 Xlib 处理器会终止整个进程——
            // 吞错留待下一次复验，best-effort 语义不能因一次 X 错误放大为壳崩溃。delegate 存静态
            // 字段防 GC 回收。安装放 try 内：失败随整体放弃并走 finally 收链。
            _ = XSetErrorHandler(s_ignoreXErrors);

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
                IntPtr inputFocus = ReadInputFocus(display);
                bool inputFocusInSubtree = inputFocus.ToInt64() > 1
                    && inputFocus != windowHandle
                    && IsSelfOrDescendant(display, windowHandle, inputFocus);
                switch (Decide(windowHandle, ReadActiveWindow(display, root, activeWindowAtom), inputFocus, inputFocusInSubtree))
                {
                    case FocusRecoveryAction.Reached:
                        return;
                    case FocusRecoveryAction.ResendActivate:
                        SendActivate(display, root, windowHandle, activeWindowAtom);
                        log($"[host] 焦点兜底：活动窗仍非本窗，{delayMs}ms 补发激活请求");
                        break;
                    case FocusRecoveryAction.SetInputFocusDirect:
                        SetInputFocus(display, windowHandle);
                        log($"[host] 焦点兜底：{delayMs}ms 复验活动窗已指向本窗但输入焦点未跟随，直落 XSetInputFocus");
                        break;
                }
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

    /// <summary>input focus 是否落在本窗子树（XQueryTree 沿父链上行，深度有界）；查询失败按非后代
    /// 处理——直落主修方向优先于理论误伤面（后代误判在复验窗内的概率与代价都远低于真失焦不修）。</summary>
    private static bool IsSelfOrDescendant(IntPtr display, IntPtr self, IntPtr window)
    {
        IntPtr current = window;
        for (int depth = 0; depth < MaxAncestryDepth; depth++)
        {
            if (current == self)
            {
                return true;
            }

            IntPtr children = IntPtr.Zero;
            if (XQueryTree(display, current, out _, out IntPtr parent, ref children, out _) == 0)
            {
                return false;
            }

            if (children != IntPtr.Zero)
            {
                _ = XFree(children);
            }

            if (parent == IntPtr.Zero)
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>直落输入焦点（<c>XSetInputFocus</c>）：对已映射可见窗由任意连接发起均合法，不经
    /// WM 焦点窃取防护的判定面；<c>RevertToPointerRoot</c> 与 Mutter 自身直落形态一致（健康态实测
    /// revert=1）。X 错误由 <see cref="s_ignoreXErrors"/> 吞掉（同步化经随后的复验往返自然消费）。</summary>
    private static void SetInputFocus(IntPtr display, IntPtr windowHandle)
    {
        _ = XSetInputFocus(display, windowHandle, RevertToPointerRoot, IntPtr.Zero);
        _ = XFlush(display);
    }

    /// <summary>读 X input focus（键盘焦点本体；None=0/PointerRoot=1 均非本窗）；revert-to 值丢弃。</summary>
    private static IntPtr ReadInputFocus(IntPtr display)
    {
        XGetInputFocus(display, out IntPtr focus, out _);
        return focus;
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

    [DllImport(LibX11, EntryPoint = "XGetInputFocus", ExactSpelling = true)]
    private static extern int XGetInputFocus(IntPtr display, out IntPtr focusReturn, out int revertToReturn);

    [DllImport(LibX11, EntryPoint = "XSetInputFocus", ExactSpelling = true)]
    private static extern int XSetInputFocus(IntPtr display, IntPtr focusWindow, int revertTo, IntPtr time);

    [DllImport(LibX11, EntryPoint = "XQueryTree", ExactSpelling = true)]
    private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr rootReturn, out IntPtr parentReturn, ref IntPtr childrenReturn, out int nchildrenReturn);

    // C 原型返回 XErrorHandler 函数指针（LP64 下 8B）——按指针对型封送，返回值丢弃。
    [DllImport(LibX11, EntryPoint = "XSetErrorHandler", ExactSpelling = true)]
    private static extern IntPtr XSetErrorHandler(XErrorHandler handler);
}
