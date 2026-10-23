namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 UI 文案词典的失败面分部（ADR native-failure-surface）：原生对话框的用户可见文案；按域立分部是为守 F1
/// （单文件 ≤400 行）。词典唯一事实源语义与门禁口径见 <see cref="UiCopy"/>。</summary>
public static partial class UiCopy
{
    /// <summary>失败面对话框标题（问题前置：按钮固定为是／否，插件默认按钮是「是」）。</summary>
    public static string RecoveryDialogTitle(bool english) => english
        ? "DeepSeek Harness Desktop — runtime recovery: quit the app?"
        : "DeepSeek Harness Desktop — 运行时恢复：要退出应用吗？";

    /// <summary>失败面对话框：stderr 尾部小节标题。</summary>
    public static string RecoveryDialogTailHeading(bool english) => english ? "Runtime stderr (tail):" : "运行时 stderr 尾部：";

    /// <summary>失败面对话框：诊断包已导出行前缀（后接路径）。</summary>
    public static string RecoveryDialogExportPrefix(bool english) => english ? "Diagnostics exported: " : "诊断包已导出：";

    /// <summary>失败面对话框：诊断包导出失败行（求助路径的退路：host.log）。</summary>
    public static string RecoveryDialogExportFailed(bool english) => english ? "Diagnostics export failed (see host.log)." : "诊断包导出失败（详见 host.log）。";

    /// <summary>失败面对话框：动作说明（按钮固定为是／否；只有显式「是」才退出）。</summary>
    public static string RecoveryDialogActions(bool english) => english
        ? "Choose Yes to quit the app; choose No to keep auto-recovering."
        : "选「是」退出应用；选「否」继续在后台自动恢复。";

    /// <summary>启动失败对话框标题（dsh 运行时起不来；新宿主进程内模态）。</summary>
    public static string StartupFailureTitle(bool english) => english
        ? "DeepSeek Harness Desktop — startup failed"
        : "DeepSeek Harness Desktop — 启动失败";

    /// <summary>启动失败原因（正文首行）：只说可确认的事实，细节指向日志。</summary>
    public static string StartupFailureReason(bool english) => english
        ? "The dsh runtime could not be started (see host.log for details)."
        : "dsh 运行时未能启动（详见 host.log）。";

    /// <summary>启动失败对话框动作说明：退出结束进程，留在窗口保留现场。</summary>
    public static string StartupFailureActions(bool english) => english
        ? "Choose \"Quit App\" to end the process; choose \"Stay\" to keep the window open (restart the app to try again)."
        : "选择「退出应用」结束进程；选择「留在窗口」保留窗口（重新启动应用即可再试）。";

    /// <summary>失败面对话框：留在窗口按钮（非破坏性动作，也是关闭框的缺省）。</summary>
    public static string DialogStayLabel(bool english) => english ? "Stay" : "留在窗口";
}
