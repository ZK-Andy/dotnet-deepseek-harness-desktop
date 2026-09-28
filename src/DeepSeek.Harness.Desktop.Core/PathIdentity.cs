namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// 平台路径等值判定（原组合根 <c>PathsEqual</c> 随细搬升 Core，ADR post-packaging-churn-restructure 余批）：
/// 「同一目录」的判定口径单点，供启动期告知等跨目录比对消费。
/// </summary>
public static class PathIdentity
{
    /// <summary>路径等值：先全路径规整再比较，Windows 不区分大小写、其余平台区分。</summary>
    /// <param name="a">待比较路径（两侧均应为绝对路径或可规整路径）。</param>
    /// <param name="b">待比较路径。</param>
    /// <returns>规整后指向同一路径则 true。</returns>
    public static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
