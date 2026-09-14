namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的启动辅助（partial）：引导进程交互面已下沉
/// <c>FirstBootBootstrapService</c>（ADR composition-root-value-flow-pipeline 批次 1），
/// 本分部只留组合根本地的小工具。
/// </summary>
public sealed partial class DesktopBootstrap
{
    /// <summary>路径等值判定（Windows 不区分大小写）——旧 home 提示的指回守卫用。</summary>
    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
