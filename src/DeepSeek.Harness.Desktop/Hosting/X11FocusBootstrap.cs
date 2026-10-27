using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// X11 首映射焦点兜底（ADR 2026-10-25-exit-kill-focus-debounce-batch、
/// 2026-10-28-first-map-input-focus-recheck、2026-10-28-first-map-surface-remap）：装后自动重启
/// 上下文实测首映射失焦——<c>_NET_ACTIVE_WINDOW</c> 悬在无 WM_CLASS 幽灵窗上，键盘面（媒体键/
/// 页内 tab/Alt-Tab）全废（键盘事件只投焦点窗）。修法三段：①首映射后向根窗口发
/// <c>_NET_ACTIVE_WINDOW</c> 激活请求（source indication=1，应用发起；与 <see cref="X11WindowDrag"/>
/// 同型的根窗口 ClientMessage）；②<b>有界复验</b>——短窗内键盘焦点本体（<c>XGetInputFocus</c>）未达
/// 本窗则按 <see cref="Decide"/> 补激活请求或直落 XSetInputFocus（10-28 实测：复验只读 EWMH 代理
/// 指标时，活动窗已指向本窗而输入焦点未跟随的背离态会被放行）；③<b>撤回重映射尾步</b>——复验收口后
/// 确定性 withdraw+map 一次：冷启动竞速下壳窗在 Mutter↔Xwayland 场景的表面输入关联可能损坏
/// （X 层焦点/形状/几何/堆叠全部健康而键盘指针不进，实测 X 层重映射即愈，与「托盘唤回自愈」同机制），
/// 重映射是表面层唯一可从壳侧发起的重建通道；重映射后恢复最大化态并重发激活。全序列有界
/// （复验 ≈2.6s + 重映射 ≈0.8s），此后不再干预（用户自行选择焦点是合法态）。
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

    /// <summary>EWMH <c>_NET_WM_STATE</c> 消息的动作码：<c>_NET_WM_STATE_ADD</c>。</summary>
    private static readonly IntPtr s_netWmStateAdd = new(1);

    /// <summary>复验时点（毫秒）：覆盖焦点窃取防护的短窗；总时长有界，不与用户的后续焦点选择竞争。</summary>
    private static readonly int[] s_recheckDelaysMs = [300, 800, 1500];

    /// <summary>重映射间隔（毫秒）：withdraw 后留窗口给 WM 完成取消管理，再 map 重建（实测 500ms 生效，取整 400）。</summary>
    private const int RemapGapMs = 400;

    /// <summary>重映射后的焦点本体复验时点（毫秒）：单次留痕，不再触发恢复动作。</summary>
    private const int RemapVerifyDelayMs = 400;

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

    /// <summary>重映射后的最大化态恢复决策（纯函数）：仅当撤回前 <c>MAXIMIZED_VERT</c>+<c>HORZ</c>
    /// 双原子同在才按最大化恢复——与 Avalonia「双原子同在才认最大化」同判据，半贴靠/单原子态不误扩。</summary>
    internal static bool DecideRemapRestore(bool maxVertPresent, bool maxHorzPresent)
    {
        return maxVertPresent && maxHorzPresent;
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

    /// <summary>发激活请求并按 <see cref="s_recheckDelaysMs"/> 复验，随后执行撤回重映射尾步：
    /// 任一步失败留痕即收（焦点缺失可由托盘唤回自愈，不值得打断）。</summary>
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

            bool focusReached = false;
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
                        focusReached = true;
                        break;
                    case FocusRecoveryAction.ResendActivate:
                        SendActivate(display, root, windowHandle, activeWindowAtom);
                        log($"[host] 焦点兜底：活动窗仍非本窗，{delayMs}ms 补发激活请求");
                        break;
                    case FocusRecoveryAction.SetInputFocusDirect:
                        SetInputFocus(display, windowHandle);
                        log($"[host] 焦点兜底：{delayMs}ms 复验活动窗已指向本窗但输入焦点未跟随，直落 XSetInputFocus");
                        break;
                }

                if (focusReached)
                {
                    break;
                }
            }

            RunSurfaceRemap(display, root, windowHandle, activeWindowAtom, log);
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

    /// <summary>撤回重映射尾步（ADR 2026-10-28-first-map-surface-remap）：冷启动竞速下壳窗在
    /// Mutter↔Xwayland 场景的表面输入关联可能损坏（X 层焦点/形状/几何/堆叠全健康而键盘指针不进，
    /// 2026-10-28 真机实测 withdraw+map 重建即愈）——复验序列收口后确定性撤回重映射一次，按撤回前
    /// 状态恢复最大化（<see cref="DecideRemapRestore"/>）并重发激活，再做单次留痕复验。</summary>
    private static void RunSurfaceRemap(IntPtr display, IntPtr root, IntPtr windowHandle, IntPtr activeWindowAtom, Action<string> log)
    {
        IntPtr wmStateAtom = XInternAtom(display, "_NET_WM_STATE", false);
        IntPtr maxVertAtom = XInternAtom(display, "_NET_WM_STATE_MAXIMIZED_VERT", false);
        IntPtr maxHorzAtom = XInternAtom(display, "_NET_WM_STATE_MAXIMIZED_HORZ", false);
        bool restoreMaximize = wmStateAtom != IntPtr.Zero && maxVertAtom != IntPtr.Zero && maxHorzAtom != IntPtr.Zero
            && DecideRemapRestore(
                ReadHasAtom(display, windowHandle, wmStateAtom, maxVertAtom),
                ReadHasAtom(display, windowHandle, wmStateAtom, maxHorzAtom));

        log("[host] 焦点兜底：撤回重映射重建窗口表面（冷启动表面关联修复）");
        _ = XWithdrawWindow(display, windowHandle, XDefaultScreen(display));
        _ = XFlush(display);
        Thread.Sleep(RemapGapMs);
        _ = XMapWindow(display, windowHandle);
        _ = XFlush(display);

        if (restoreMaximize)
        {
            SendRootClientMessage(display, root, windowHandle, wmStateAtom, s_netWmStateAdd, maxVertAtom, maxHorzAtom, (IntPtr)1);
            _ = XFlush(display);
        }

        SendActivate(display, root, windowHandle, activeWindowAtom);
        Thread.Sleep(RemapVerifyDelayMs);
        IntPtr inputFocus = ReadInputFocus(display);
        bool inputFocusInSubtree = inputFocus.ToInt64() > 1
            && inputFocus != windowHandle
            && IsSelfOrDescendant(display, windowHandle, inputFocus);
        bool reached = Decide(windowHandle, ReadActiveWindow(display, root, activeWindowAtom), inputFocus, inputFocusInSubtree)
            == FocusRecoveryAction.Reached;
        log(reached
            ? "[host] 焦点兜底：重映射完成，焦点本体已到位"
            : "[host] 焦点兜底：重映射完成，焦点本体未跟随（留痕，不再干预）");
    }

    /// <summary>向根窗口发 EWMH ClientMessage（format 32，data.l 四槽）。激活请求（<c>_NET_ACTIVE_WINDOW</c>）
    /// 为 l[0]=source indication（1=应用发起）、l[1]=timestamp（0=CurrentTime）、l[2]=当前活动窗（0）、l[3]=0；
    /// <c>_NET_WM_STATE</c> 为 l[0]=动作、l[1]/l[2]=属性原子、l[3]=source indication。</summary>
    private static void SendRootClientMessage(IntPtr display, IntPtr root, IntPtr windowHandle, IntPtr messageAtom, IntPtr data0, IntPtr data1, IntPtr data2, IntPtr data3)
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
            Marshal.WriteInt64(xevent, 56, data0.ToInt64());
            Marshal.WriteInt64(xevent, 64, data1.ToInt64());
            Marshal.WriteInt64(xevent, 72, data2.ToInt64());
            Marshal.WriteInt64(xevent, 80, data3.ToInt64());

            _ = XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, xevent);
            _ = XFlush(display);
        }
        finally
        {
            Marshal.FreeHGlobal(xevent);
        }
    }

    /// <summary>激活请求便捷形：<_NET_ACTIVE_WINDOW> 四槽填法见 <see cref="SendRootClientMessage"/>。</summary>
    private static void SendActivate(IntPtr display, IntPtr root, IntPtr windowHandle, IntPtr messageAtom)
    {
        SendRootClientMessage(display, root, windowHandle, messageAtom, (IntPtr)1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>读窗口 <paramref name="stateAtom"/> 属性（ATOM 数组，format 32 在 LP64 按 long 单元返回），
    /// 判是否含 <paramref name="atom"/>；读失败/空属性按不含处理。</summary>
    private static bool ReadHasAtom(IntPtr display, IntPtr window, IntPtr stateAtom, IntPtr atom)
    {
        if (XGetWindowProperty(
                display, window, stateAtom, 0, 32, false, AnyPropertyType,
                out _, out int actualFormat, out IntPtr nitems, out _, out IntPtr prop) != 0)
        {
            return false;
        }

        try
        {
            if (actualFormat != 32 || prop == IntPtr.Zero)
            {
                return false;
            }

            long count = nitems.ToInt64();
            for (long i = 0; i < count; i++)
            {
                if (Marshal.ReadInt64(prop, checked((int)(i * sizeof(long)))) == atom.ToInt64())
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (prop != IntPtr.Zero)
            {
                _ = XFree(prop);
            }
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

    [DllImport(LibX11, EntryPoint = "XWithdrawWindow", ExactSpelling = true)]
    private static extern int XWithdrawWindow(IntPtr display, IntPtr window, int screenNumber);

    [DllImport(LibX11, EntryPoint = "XMapWindow", ExactSpelling = true)]
    private static extern int XMapWindow(IntPtr display, IntPtr window);

    [DllImport(LibX11, EntryPoint = "XDefaultScreen", ExactSpelling = true)]
    private static extern int XDefaultScreen(IntPtr display);

    // C 原型返回 XErrorHandler 函数指针（LP64 下 8B）——按指针对型封送，返回值丢弃。
    [DllImport(LibX11, EntryPoint = "XSetErrorHandler", ExactSpelling = true)]
    private static extern IntPtr XSetErrorHandler(XErrorHandler handler);
}
