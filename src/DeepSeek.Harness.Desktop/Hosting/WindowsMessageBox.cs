using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// Windows 原生是/否提示（<c>MessageBoxW</c>；ADR avalonia-g4-webview2-preflight）：WebView2
/// 运行时缺失时 Avalonia 尚未启动、也没有可用的属主窗口，故用无属主原生模态，而不是进程内
/// <c>FailureDialogWindow</c>——后者需要先起完整 UI 生命周期，恰是本检测要避免的失败面。
/// 只有显式「是」返回 true；关闭框与其它结果返回 false。调用方须在 Windows 上调用（编译期
/// <see cref="System.Runtime.Versioning.SupportedOSPlatformAttribute"/> 约束）。
/// </summary>
internal static class WindowsMessageBox
{
    private const int IdYes = 6; // Win32 IDYES
    private const uint Style = 0x00000004 | 0x00000010 | 0x00010000 | 0x00040000; // 是/否 + 错误图标 + 置前

    /// <summary>弹出提示，返回用户是否选择「是」。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <returns>true = 用户选择「是」。</returns>
    [SupportedOSPlatform("windows")]
    public static bool AskYes(string title, string message) =>
        MessageBoxW(IntPtr.Zero, message, title, Style) == IdYes;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
