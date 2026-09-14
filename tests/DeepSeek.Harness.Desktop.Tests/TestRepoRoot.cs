namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>测试期仓库根定位（以解决方案文件为锚）：组合根源序测试与架构测试共用的单点，
/// 避免同一段目录上溯逻辑在多处逐字重复。</summary>
internal static class TestRepoRoot
{
    /// <summary>从测试程序集位置向上查找含 <c>dotnet-deepseek-harness-desktop.slnx</c> 的目录。</summary>
    /// <returns>仓库根绝对路径。</returns>
    /// <exception cref="InvalidOperationException">上溯到文件系统根仍未找到解决方案文件（fail loud）。</exception>
    public static string Find()
    {
        DirectoryInfo? dir = new(typeof(TestRepoRoot).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnet-deepseek-harness-desktop.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("仓库根未找到：测试程序集之上无 dotnet-deepseek-harness-desktop.slnx");
    }
}
