using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// X11 窗口身份（WM_CLASS）：desktop entry <c>StartupWMClass</c> 的对侧。Avalonia 默认把 WM_CLASS
/// 设为程序集名，与打包面的 desktop entry 断裂，启动器/Alt-Tab 的图标匹配链失效（真机 xprop 实证）。
/// 句柄就绪后显式写 XA_WM_CLASS（两个 null 结尾字符串，instance 与 class 同值）——
/// Avalonia 12 的 X11 后端无公开 WM_CLASS API，走与 <see cref="X11WindowDrag"/> 同型的独立 X11 连接。
/// 值与 <c>scripts/package-linux.sh</c> 的 <c>StartupWMClass</c> 是跨语言同一事实，两侧必须同步改
/// （脚本内有注释锚指回本常量）。
/// </summary>
internal static class X11WindowIdentity
{
    /// <summary>窗口类名（打包面 StartupWMClass 的对侧锚，两侧同步改）。</summary>
    public const string WmClass = "Dotnet.Deepseek.Harness.Desktop";

    private const string LibX11 = "libX11.so.6";
    private const int PropModeReplace = 0;

    /// <summary>给窗口写 WM_CLASS（best-effort）：非 Linux、句柄缺失或 libX11 不可用都静默跳过——
    /// 身份缺失只影响启动器匹配，不值得打断窗口生命周期。</summary>
    /// <param name="window">目标窗口（须已显示且有平台句柄）。</param>
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

        byte[] payload = BuildPayload(WmClass, WmClass);
        IntPtr display;
        IntPtr data = IntPtr.Zero;
        try
        {
            // libX11 装载形态异常（缺失/坏镜像/入口缺失）一律按「本机不可写身份」收口——
            // 身份缺失只影响启动器匹配，绝不逃进 OnOpened 打断窗口打开
            display = XOpenDisplay(null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return;
        }

        if (display == IntPtr.Zero)
        {
            return;
        }

        try
        {
            data = Marshal.AllocHGlobal(payload.Length);
            Marshal.Copy(payload, 0, data, payload.Length);
            IntPtr wmClass = XInternAtom(display, "WM_CLASS", false);
            IntPtr xString = XInternAtom(display, "STRING", false);
            if (wmClass == IntPtr.Zero || xString == IntPtr.Zero)
            {
                return;
            }

            _ = XChangeProperty(display, handle, wmClass, xString, 8, PropModeReplace, data, payload.Length);
            _ = XFlush(display);
        }
        catch (Exception ex)
        {
            // 所吞=写属性期的库级异常（坏 atom/连接已断等）：仅影响启动器图标匹配，留痕即可
            log($"[host] WM_CLASS 写入失败（仅影响启动器图标匹配）：{ex.Message}");
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(data);
            }

            _ = XCloseDisplay(display);
        }
    }

    /// <summary>构造 WM_CLASS 属性字节：两个 null 结尾字符串拼接（instance、class；末字节为终止符）。</summary>
    /// <param name="instance">实例名（资源名）。</param>
    /// <param name="klass">类名（<c>StartupWMClass</c> 匹配对象）。</param>
    /// <returns>属性字节序列。</returns>
    internal static byte[] BuildPayload(string instance, string klass) =>
        Encoding.ASCII.GetBytes($"{instance}\0{klass}\0");

    [DllImport(LibX11, EntryPoint = "XOpenDisplay", ExactSpelling = true)]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(LibX11, EntryPoint = "XInternAtom", ExactSpelling = true)]
    private static extern IntPtr XInternAtom(IntPtr display, string atomName, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport(LibX11, EntryPoint = "XChangeProperty", ExactSpelling = true)]
    private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, IntPtr data, int nelements);

    [DllImport(LibX11, EntryPoint = "XFlush", ExactSpelling = true)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibX11, EntryPoint = "XCloseDisplay", ExactSpelling = true)]
    private static extern int XCloseDisplay(IntPtr display);
}
