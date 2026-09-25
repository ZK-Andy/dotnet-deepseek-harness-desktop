namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 UI 文案单一词典（批次 A「表示先行」）：托盘菜单、横幅、恢复页、引导过渡页等
/// <b>用户可见</b>文案的唯一家。随 locale 切换的文案以 <c>(english, zh)</c> 双参入口提供；
/// 静态页（<c>wwwroot/index.html</c>）的中文文案以登记常量收容——词典是唯一事实源，
/// <c>index.html</c> 是被门禁核对的消费方（<c>scripts/verify-ui-copy.py</c>：
/// 消费文件不得再出现词典之外的 CJK 字面量）。
/// host.log 诊断行不属 UI 文案，不入本词典。
/// 覆盖面无批次遗留：托盘检查通知正文与引导失败 reason（<c>RuntimeBootstrap</c>）均在此，
/// 引导页（<c>index.html</c>）的中英双语以 zh/en 成对常量登记——消费方与词典的每一侧都由
/// 门禁核对（<c>index.html</c> 不得出现未登记文案，登记文案也不得从页面上消失）。
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

    // ======== 托盘「检查更新」结果通知（TrayCheckFeedback） ========

    /// <summary>托盘通知：已是最新版本。</summary>
    public static string TrayCheckUpToDate(bool english) => english ? "Already up to date" : "已是最新版本";

    /// <summary>托盘通知：新版本就绪，可在设置页安装。</summary>
    public static string TrayCheckReady(string version, bool english) => english
        ? $"New version {version} is ready; install it in Settings → Desktop Settings."
        : $"新版本 {version} 已就绪，可在 设置 → 桌面设置 安装";

    /// <summary>托盘通知：检查更新失败前缀（后接原因）。</summary>
    public static string TrayCheckFailedPrefix(bool english) => english ? "Update check failed: " : "检查更新失败：";

    // ======== 引导失败原因（RuntimeBootstrap → wwwroot 引导页错误框） ========

    /// <summary>引导：npm 装全局 dsh 需提升权限（附手动命令）。</summary>
    public static string BootstrapNpmNeedsElevation(string dshSpec, string detail, bool english) => english
        ? $"npm install needs elevated permissions. Run it manually in a terminal: sudo npm install -g {dshSpec} ({detail})"
        : $"npm install 需要提升权限。请在终端手动执行：sudo npm install -g {dshSpec}（{detail}）";

    /// <summary>引导：npm install 非零退出。</summary>
    public static string BootstrapNpmFailed(int exit, string detail, bool english) => english
        ? $"npm install failed with exit={exit}: {detail}"
        : $"npm install 失败 exit={exit}：{detail}";

    /// <summary>引导：装完仍解析不到全局 dsh 版本（PATH/前缀错位指引）。</summary>
    public static string BootstrapDshUnresolved(string dshSpec, bool english) => english
        ? $"Could not resolve the global dsh version after install ({dshSpec}). Make sure the npm global bin of that node (the bin under `npm config get prefix`) is on PATH, or run `npm install -g {dshSpec}` manually."
        : $"安装后未能解析全局 dsh 版本（{dshSpec}）。请确认该 node 的 npm 全局 bin（`npm config get prefix` 下的 bin）已加入 PATH，或手动执行 `npm install -g {dshSpec}`。";

    /// <summary>引导：DshSpec 非 registry 形态（caret range 等 pnpm 不支持形，ADR pnpm-caret-spec-rejection）。</summary>
    public static string BootstrapInvalidDshSpec(string dshSpec, string reason, bool english) => english
        ? $"Invalid DshSpec ({dshSpec}): {reason}. Use a registry spec like @deepseek-ai/dsh@alpha."
        : $"DshSpec 非法（{dshSpec}）：{reason}。请用 registry 形态，如 @deepseek-ai/dsh@alpha。";

    /// <summary>引导：当前平台无 Node 发行包坐标。</summary>
    public static string BootstrapUnsupportedPlatform(bool english) => english
        ? "No Node distribution archive coordinates for this platform"
        : "当前平台无对应的 Node 发行包坐标（fail loud）";

    /// <summary>引导：Node 发行包 SHA256 与官方摘要不符（安全中止）。</summary>
    public static string BootstrapNodeHashMismatch(string expected, string actual, bool english) => english
        ? $"Node distribution SHA256 mismatch (expected {expected}, actual {actual}); aborting for safety"
        : $"Node 发行包 SHA256 不匹配（期望 {expected}，实际 {actual}），安全中止";

    /// <summary>引导：Node 发行包解压后无内容目录。</summary>
    public static string BootstrapNodeDistEmpty(bool english) => english
        ? "Node distribution archive extracted no content directory"
        : "Node 发行包解压结果无内容目录";

    /// <summary>引导：Node 落位后入口缺失（布局异常）。</summary>
    public static string BootstrapNodeEntryMissing(string prefix, bool english) => english
        ? $"node entry missing after installing into the system-global {prefix} (layout anomaly)"
        : $"node 装到系统全局 {prefix} 后入口缺失（布局异常）";

    /// <summary>引导：系统全局 node 安装位不可写（权限不足，附管理员指引）。</summary>
    public static string BootstrapNodeInstallDenied(string prefix, string detail, bool english) => english
        ? $"Cannot write to the system-global node location {prefix} (permission denied: {detail}). Install Node.js with administrator rights (official installer at https://nodejs.org or your package manager), or grant write access to {prefix} and retry."
        : $"无法写入系统全局 node 安装位 {prefix}（权限不足：{detail}）。请以管理员权限安装 Node.js（https://nodejs.org 官方安装包/系统包管理器），或放开 {prefix} 写入权限后重试。";

    /// <summary>引导：nodejs.org index.json 解析不出最新 Node 版本。</summary>
    public static string BootstrapNodeVersionUnresolved(bool english) => english
        ? "Could not parse the latest Node version from the nodejs.org index.json"
        : "无法从 nodejs.org index.json 解析最新 Node 版本";

    /// <summary>引导：取官方 SHA256 摘要失败。</summary>
    public static string BootstrapShasumsFetchFailed(string detail, bool english) => english
        ? $"Failed to fetch the official Node distribution SHA256 digest: {detail}"
        : $"获取 Node 发行包官方 SHA256 摘要失败：{detail}";

    /// <summary>引导：SHASUMS256.txt 缺目标文件摘要（安全中止）。</summary>
    public static string BootstrapShasumsMissing(string fileName, bool english) => english
        ? $"SHASUMS256.txt has no digest for {fileName}; aborting for safety"
        : $"SHASUMS256.txt 缺少 {fileName} 的摘要，安全中止";

    /// <summary>引导：Node 发行包所有候选源下载失败。</summary>
    public static string BootstrapNodeDownloadFailed(string failures, bool english) => english
        ? $"Node distribution download: every candidate source failed ({failures})"
        : $"Node 发行包下载：所有候选源均失败（{failures}）";

    /// <summary>引导：tar 解压失败。</summary>
    public static string BootstrapExtractFailed(int exit, string detail, bool english) => english
        ? $"tar extraction failed with exit={exit}: {detail}"
        : $"tar 解压失败 exit={exit}：{detail}";

    /// <summary>引导：子进程启动失败。</summary>
    public static string BootstrapProcessStartFailed(string exe, bool english) => english
        ? $"Cannot start process {exe}"
        : $"无法启动进程 {exe}";

    /// <summary>引导：步骤级超时（网络停滞/资源受限，可重试）。</summary>
    public static string BootstrapStepTimeout(int minutes, bool english) => english
        ? $"Step timed out (>{minutes} min): the network stalled or resources are constrained — you can retry"
        : $"步骤超时（>{minutes} 分钟），网络停滞或资源受限，可重试";

    /// <summary>引导：失败但无原因文本时的兜底。</summary>
    public static string BootstrapUnknownError(bool english) => english ? "Unknown error" : "未知错误";

    /// <summary>引导：单一下载源失败明细（多源失败时逐条拼接的条目）。</summary>
    public static string BootstrapDownloadSourceFailed(string url, string detail, bool english) => english
        ? $"{url}: {detail}"
        : $"{url}：{detail}";

    // ======== 插件引导结果（FirstBootBootstrapService → 引导页日志区/进度行） ========

    /// <summary>插件引导：用户跳过（与引导页兜底文案同句，单一事实源在 <see cref="IndexPreinstallSkipped"/>）。</summary>
    public static string PreinstallSkippedMessage(bool english) => english ? IndexPreinstallSkippedEn : IndexPreinstallSkipped;

    /// <summary>插件引导：安装完成（与引导页兜底文案同句，单一事实源在 <see cref="IndexPreinstallDone"/>）。</summary>
    public static string PreinstallDoneMessage(bool english) => english ? IndexPreinstallDoneEn : IndexPreinstallDone;

    /// <summary>插件引导：安装未成功（详见日志）。</summary>
    public static string PreinstallFailedMessage(bool english) => english
        ? "Install did not succeed (see log)"
        : "安装未成功（见日志）";

    /// <summary>插件引导：进度行「可选插件准备」。</summary>
    public static string PreinstallStepPreparing(bool english) => english
        ? "Preparing optional plugins"
        : "可选插件准备";

    /// <summary>插件引导：进度行「插件准备完成」。</summary>
    public static string PreinstallStepReady(bool english) => english
        ? "Plugin setup complete"
        : "插件准备完成";

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

    // ======== 静态引导页登记（wwwroot/index.html；zh 常量 + EN 字典，被 verify-ui-copy 双向核对） ========
    // index.html 是静态文档，文案改动必须同步此登记（zh 字面量留在 HTML、英文收在 EN 字典），否则 verify-ui-copy 拦截。

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

    // ---- 引导页英文侧（index.html 的 EN 字典；与上方 zh 常量一一对应） ----

    /// <summary>引导页 EN：确保系统全局 Node 步骤。</summary>
    public const string IndexStepEnsureNodeEn = "Ensure system-global Node";

    /// <summary>引导页 EN：npm 安装全局 dsh 步骤。</summary>
    public const string IndexStepInstallDshEn = "Install global dsh via npm (network required)";

    /// <summary>引导页 EN：验证 dsh 版本步骤。</summary>
    public const string IndexStepVerifyDshEn = "Verify dsh version";

    /// <summary>引导页 EN：插件准备步骤。</summary>
    public const string IndexStepPreinstallPluginsEn = "Plugin setup";

    /// <summary>引导页 EN：可选插件小节标题。</summary>
    public const string IndexOptionalPluginsTitleEn = "Optional plugins";

    /// <summary>引导页 EN：可选插件说明。</summary>
    public const string IndexOptionalPluginsNoteEn =
        "These plugins enhance the DeepSeek Harness desktop experience — install any you want (skipping still lets you add them later from the in-app market):";

    /// <summary>引导页 EN：确认安装按钮。</summary>
    public const string IndexPreinstallConfirmEn = "Install";

    /// <summary>引导页 EN：跳过按钮。</summary>
    public const string IndexPreinstallSkipEn = "Skip";

    /// <summary>引导页 EN：重试按钮。</summary>
    public const string IndexBootRetryEn = "Retry";

    /// <summary>引导页 EN：运行时未就绪回退行前段。</summary>
    public const string IndexFallbackTextEn = "Runtime not ready (";

    /// <summary>引导页 EN：运行时未就绪回退行尾段。</summary>
    public const string IndexFallbackTextTailEn = "did not yield a URL in time).";

    /// <summary>引导页 EN：回退提示行前段。</summary>
    public const string IndexFallbackHintEn = "Check";

    /// <summary>引导页 EN：回退提示行尾。</summary>
    public const string IndexFallbackHintTailEn = "and the logs, or restart the app.";

    /// <summary>引导页 EN：dshmarket 插件显示名。</summary>
    public const string IndexPluginNameDshmarketEn = "Plugin market (dshmarket)";

    /// <summary>引导页 EN：安装中状态。</summary>
    public const string IndexInstallingEn = "Installing…";

    /// <summary>引导页 EN：全部可选项已跳过状态。</summary>
    public const string IndexPreinstallSkippedAllEn = "Optional plugins skipped";

    /// <summary>引导页 EN：未知插件名兜底。</summary>
    public const string IndexPluginFallbackEn = "plugin";

    /// <summary>引导页 EN：安装中前缀（后接插件名）。</summary>
    public const string IndexInstallingPrefixEn = "Installing ";

    /// <summary>引导页 EN：单项已跳过状态。</summary>
    public const string IndexPreinstallSkippedEn = "Plugin install skipped";

    /// <summary>引导页 EN：安装完成状态。</summary>
    public const string IndexPreinstallDoneEn = "Install complete";

    /// <summary>引导页 EN：推荐徽标前缀（后接插件名）。</summary>
    public const string IndexRecommendedPrefixEn = "Recommended · ";
}
