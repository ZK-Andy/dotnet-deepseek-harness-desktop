namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 旧 home 探测（ADR shared-home-desktop-profile）：v0.2.x 私有 home 仍在时于 host.log
/// 留痕数据位置切换事实。界面横幅已按用户拍板去除（2026-08-24，见
/// ADR companion-settings-consolidation）——本类只剩只读探测面。
/// 回退通道 = 桌面专属覆盖环境变量 <see cref="HarnessRuntimeHost.HomeOverrideEnv"/> 指回旧目录。
/// </summary>
public static class LegacyHomeNotice
{
    /// <summary>v0.2.x 私有 home（历史默认值 <c>&lt;LocalApplicationData&gt;/DeepSeek.Harness.Desktop/dsh</c>）。</summary>
    public static string LegacyPrivateHome =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeek.Harness.Desktop",
            "dsh");

    /// <summary>旧私有 home 是否仍存在（只读探测）。</summary>
    public static bool IsPresent() => Directory.Exists(LegacyPrivateHome);
}
