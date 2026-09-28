namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>平台路径等值判定（Windows 不区分大小写）——启动期告知的旧 home 指回守卫口径。</summary>
public class PathIdentityTests
{
    /// <summary>同一路径经点段（. / ..）规整后等值；GetFullPath 不剥尾分隔符，两侧同为规整形态即相等。</summary>
    [Fact]
    public void SamePath_AcrossDotSegments_IsEqual()
    {
        // GetFullPath 规整掉点段（. / ..）与重复分隔符，但不剥尾分隔符——两侧同为规整形态即相等
        Assert.True(PathIdentity.PathsEqual("/tmp/a/./b", "/tmp/a/b"));
        Assert.True(PathIdentity.PathsEqual("/tmp/a/../a/b/", "/tmp/a/b/"));
    }

    /// <summary>指向不同目录的两路径不等值。</summary>
    [Fact]
    public void DifferentPaths_AreNotEqual()
    {
        Assert.False(PathIdentity.PathsEqual("/tmp/a", "/tmp/b"));
    }

    /// <summary>Windows 路径大小写不敏感、其余平台敏感——平台分支两侧语义都钉住。</summary>
    [Fact]
    public void WindowsPaths_CompareCaseInsensitive_OnWindows()
    {
        // 平台分支：Windows 上大小写不敏感，其余平台敏感（随平台跑对应分支，两语义都有断言）
        bool equal = PathIdentity.PathsEqual("/tmp/Data", "/tmp/data");
        Assert.Equal(OperatingSystem.IsWindows(), equal);
    }
}
