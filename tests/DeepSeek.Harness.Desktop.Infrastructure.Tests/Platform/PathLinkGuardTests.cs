namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>
/// 路径链接判定的回归（ADR profile-lock-path-symlink-rejection）：文件链/目录链识别、
/// 常规与缺失路径不误报、悬空链仍被判为链接（探测失败不得退化成「非链接」）。
/// 符号链接用例按 <c>RunMarkerTests</c> 惯例仅 Linux 断言（Windows 建链需特权）。
/// </summary>
public class PathLinkGuardTests
{
    /// <summary>验证常规文件与常规目录都不被判为链接。</summary>
    [Fact]
    public void IsLink_RegularPaths_ReturnFalse()
    {
        string root = NewDir();
        try
        {
            string file = Path.Combine(root, "plain.txt");
            string dir = Path.Combine(root, "sub");
            File.WriteAllText(file, "x");
            Directory.CreateDirectory(dir);

            Assert.False(PathLinkGuard.IsLink(file), "常规文件不是链接");
            Assert.False(PathLinkGuard.IsLink(dir), "常规目录不是链接");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>验证缺失路径返回 false——不存在不得被误判成链接。此例全平台运行，钉住初版缺陷：
    /// 直接读 `FileInfo.Attributes` 时，Unix 对缺失路径返回 `(FileAttributes)(-1)`（ReparsePoint 位为真），
    /// 会把缺失的下载锁当链接拒绝，导致全新安装被挡。</summary>
    [Fact]
    public void IsLink_MissingPath_ReturnFalse()
    {
        string root = NewDir();
        try
        {
            Assert.False(PathLinkGuard.IsLink(Path.Combine(root, "absent.txt")));
            Assert.False(PathLinkGuard.IsLink(Path.Combine(root, "absent-dir")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>验证文件符号链接与目录符号链接都被识别（仅 Linux 断言）。</summary>
    [Fact]
    public void IsLink_Symlinks_ReturnTrue()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 建链特权与链接语义按 Linux 断言
        }

        string root = NewDir();
        try
        {
            string targetFile = Path.Combine(root, "target.txt");
            string targetDir = Path.Combine(root, "target-dir");
            File.WriteAllText(targetFile, "x");
            Directory.CreateDirectory(targetDir);

            string linkFile = Path.Combine(root, "link.txt");
            string linkDir = Path.Combine(root, "link-dir");
            File.CreateSymbolicLink(linkFile, targetFile);
            Directory.CreateSymbolicLink(linkDir, targetDir);

            Assert.True(PathLinkGuard.IsLink(linkFile), "文件符号链接应被识别");
            Assert.True(PathLinkGuard.IsLink(linkDir), "目录符号链接应被识别");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>验证悬空符号链接仍被识别为链接——探测目标属性会抛 IO 异常，不得因此退化成「非链接」放行。</summary>
    [Fact]
    public void IsLink_DanglingSymlink_ReturnTrue()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 建链特权与链接语义按 Linux 断言
        }

        string root = NewDir();
        try
        {
            string link = Path.Combine(root, "dangling");
            File.CreateSymbolicLink(link, Path.Combine(root, "does-not-exist"));

            Assert.True(PathLinkGuard.IsLink(link), "悬空链仍须判为链接（否则守卫被绕过）");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>验证折算式：有链接目标即为链接（悬空链属性不可得也算）；无目标时仅重解析点算链接
    /// ——含 Windows junction 分支，故全平台覆盖（真实链接用例受建链特权限制，仅 Linux）。</summary>
    [Theory]
    [InlineData("target", null, true)]
    [InlineData("target", FileAttributes.Normal, true)]
    [InlineData(null, FileAttributes.ReparsePoint, true)]
    [InlineData(null, FileAttributes.Normal, false)]
    [InlineData(null, null, false)]
    public void IsLinkFrom_ProbeCombinations(string? linkTarget, FileAttributes? attributes, bool expected)
    {
        Assert.Equal(expected, PathLinkGuard.IsLinkFrom(linkTarget, attributes));
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "linkguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
