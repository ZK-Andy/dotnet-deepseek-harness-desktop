namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主 UI 文案单一词典（批次 A「表示先行」）：托盘菜单、横幅、恢复页、引导过渡页等
/// <b>用户可见</b>文案的唯一家。随 locale 切换的文案以 <c>(english, zh)</c> 双参入口提供；
/// 静态页（<c>wwwroot/index.html</c>）的中文文案以登记常量收容——词典是唯一事实源，
/// <c>index.html</c> 是被门禁核对的消费方（<c>scripts/verify-ui-copy.py</c>：
/// 消费文件不得再出现词典之外的 CJK 字面量）。
/// host.log 诊断行不属 UI 文案，不入本词典。
/// 批次边界（R2 建议存证）：TrayCheckFeedback 通知正文与 RuntimeBootstrap 失败 reason 仍是
/// 用户可见但未收编的存量——随下一文案批次迁入，届时同步扩 verify-ui-copy 的消费清单。
/// </summary>
/// <remarks>
/// 词典选型（ADR review-to-machine-gates）：强类型静态类而非 resx——本壳仅中/英两分支、
/// 由 <see cref="UiLocale.IsEnglish"/> 判定，resx 的卫星装配与 ResourceManager 文化链
/// 对两分支场景是纯开销；强类型入口让编译器（而非字符串键）保证引用完整性。
/// </remarks>
public static class UiCopy
{
    // ======== 托盘菜单（TrayMenuActions） ========

    /// <summary>托盘：显示主窗。</summary>
    public static string TrayShowMainWindow(bool english) => english ? "Show Main Window" : "显示主窗";

    /// <summary>托盘：检查更新。</summary>
    public static string TrayCheckForUpdates(bool english) => english ? "Check for Updates" : "检查更新";

    /// <summary>托盘：退出。</summary>
    public static string TrayQuit(bool english) => english ? "Quit" : "退出";

    // ======== 横幅（DesktopBanner / UpdateBanner / UiLocale） ========

    /// <summary>横幅确认按钮（横幅共用，随当前 locale）。</summary>
    public static string OkLabel(bool english) => english ? "OK" : "知道了";

    /// <summary>自更新就绪横幅正文。</summary>
    public static string UpdateReadyText(string version, bool english) => english
        ? $"New version {version} is ready. Install it in Settings → Desktop Settings."
        : "新版本 " + version + " 已就绪，可在 设置 → 桌面设置 中一键安装。";

    /// <summary>dsh 版本底线横幅正文（检测版本低于底线）。</summary>
    public static string VersionFloorBannerText(string detectedVersion, string minimumVersion, bool english) => english
        ? $"Current dsh version {detectedVersion} is below the minimum supported by this desktop ({minimumVersion}); data or behavior may be incompatible. Upgrade dsh to the alpha channel."
        : "当前 dsh 版本 " + detectedVersion +
          " 低于桌面支持的最低版本 " + minimumVersion +
          "，可能出现数据或行为不兼容；请升级 dsh 至 alpha 通道。";

    /// <summary>非受控退出横幅正文。</summary>
    public static string UncleanExitBannerText(bool english) => english
        ? "The app did not exit cleanly last time (e.g. the process was killed). If it behaves oddly, export diagnostics in Settings."
        : "上次运行未正常退出（如手动结束进程）。若应用行为异常，请在设置页导出诊断信息。";

    // ======== 恢复页（RecoveryPageBuilder） ========

    /// <summary>恢复页：导出诊断包按钮。</summary>
    public static string RecoveryExportButton(bool english) => english ? "Export Diagnostics" : "导出诊断包";

    /// <summary>恢复页：退出应用按钮。</summary>
    public static string RecoveryExitButton(bool english) => english ? "Quit App" : "退出应用";

    /// <summary>恢复页：自动重试说明行。</summary>
    public static string RecoveryAutoRetryNote(bool english) => english
        ? "Auto-retry in progress; this page will disappear once recovered."
        : "系统正在自动重试；恢复后本页会自动消失。";

    /// <summary>恢复页：导出中状态。</summary>
    public static string RecoveryExporting(bool english) => english ? "Exporting…" : "正在导出…";

    /// <summary>恢复页：已导出状态前缀（后接路径数据）。</summary>
    public static string RecoveryExportedPrefix(bool english) => english ? "Exported: " : "已导出：";

    /// <summary>恢复页：导出失败状态前缀（后接原因数据）。</summary>
    public static string RecoveryExportFailedPrefix(bool english) => english ? "Export failed: " : "导出失败：";

    /// <summary>恢复页：未知原因兜底。</summary>
    public static string RecoveryUnknownReason(bool english) => english ? "unknown reason" : "未知原因";

    /// <summary>恢复页：崩溃原因（宿主侧注入，随包英文文案）。</summary>
    public static string ReasonRuntimeCrashed(bool english) => english
        ? "The runtime process exited unexpectedly; restarting…"
        : "运行时进程意外退出，正在自动重启";

    // ======== 过渡页（RecoveryPageBuilder.RestartingSkeleton） ========

    /// <summary>过渡页：运行时重启说明行。</summary>
    public static string RestartingNote(bool english) => english
        ? "Runtime restarting, reconnecting…"
        : "运行时重启中，正在重新连接…";

    // ======== 静态引导页登记（wwwroot/index.html；zh 单语，被 verify-ui-copy 核对） ========
    // index.html 是静态文档，文案改动必须同步此登记，否则 verify-ui-copy 拦截。

    /// <summary>引导页：确保系统全局 Node 步骤。</summary>
    public const string IndexStepEnsureNode = "确保系统全局 Node";

    /// <summary>引导页：npm 安装全局 dsh 步骤。</summary>
    public const string IndexStepInstallDsh = "npm 安装全局 dsh（需联网）";

    /// <summary>引导页：验证 dsh 版本步骤。</summary>
    public const string IndexStepVerifyDsh = "验证 dsh 版本";

    /// <summary>引导页：插件准备步骤。</summary>
    public const string IndexStepPreinstallPlugins = "插件准备";

    /// <summary>引导页：可选插件小节标题。</summary>
    public const string IndexOptionalPluginsTitle = "可选插件";

    /// <summary>引导页：可选插件说明。</summary>
    public const string IndexOptionalPluginsNote =
        "以下插件为 DeepSeek Harness 桌面体验提供增强，可自由选择安装（跳过仍可稍后在应用内补装）：";

    /// <summary>引导页：确认安装按钮。</summary>
    public const string IndexPreinstallConfirm = "确认安装";

    /// <summary>引导页：跳过按钮。</summary>
    public const string IndexPreinstallSkip = "跳过";

    /// <summary>引导页：重试按钮。</summary>
    public const string IndexBootRetry = "重试";

    /// <summary>引导页：运行时未就绪回退行前段（后接 <c>&lt;code&gt;dsh web&lt;/code&gt;</c>）。</summary>
    public const string IndexFallbackText = "运行时未就绪（";

    /// <summary>引导页：运行时未就绪回退行尾段。</summary>
    public const string IndexFallbackTextTail = "未在时限内给出 URL）。";

    /// <summary>引导页：回退提示行前段（后接 <c>&lt;code&gt;DEEPSEEK_API_KEY&lt;/code&gt;</c>）。</summary>
    public const string IndexFallbackHint = "请检查";

    /// <summary>引导页：回退提示行尾。</summary>
    public const string IndexFallbackHintTail = "与日志，或重启应用。";

    /// <summary>引导页脚本：dshmarket 插件显示名（PLUGIN_NAMES）。</summary>
    public const string IndexPluginNameDshmarket = "插件市场（dshmarket）";

    /// <summary>引导页脚本：安装中状态。</summary>
    public const string IndexInstalling = "正在安装…";

    /// <summary>引导页脚本：全部可选项已跳过状态。</summary>
    public const string IndexPreinstallSkippedAll = "已跳过可选插件";

    /// <summary>引导页脚本：未知插件名兜底。</summary>
    public const string IndexPluginFallback = "插件";

    /// <summary>引导页脚本：安装中前缀（后接插件名）。</summary>
    public const string IndexInstallingPrefix = "正在安装 ";

    /// <summary>引导页脚本：单项已跳过状态。</summary>
    public const string IndexPreinstallSkipped = "已跳过插件安装";

    /// <summary>引导页脚本：安装完成状态。</summary>
    public const string IndexPreinstallDone = "安装完成";

    /// <summary>引导页脚本：推荐徽标前缀（后接插件名）。</summary>
    public const string IndexRecommendedPrefix = "推荐 · ";
}
