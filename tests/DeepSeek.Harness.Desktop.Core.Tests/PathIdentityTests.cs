namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>平台路径等值判定（Windows 不区分大小写）——启动期告知的旧 home 指回守卫口径。</summary>
public class PathIdentityTests
{
    [Fact]
    public void SamePath_AcrossDotSegments_IsEqual()
    {
        // GetFullPath 规整掉点段（. / ..）与重复分隔符，但不剥尾分隔符——两侧同为规整形态即相等
        Assert.True(PathIdentity.PathsEqual("/tmp/a/./b", "/tmp/a/b"));
        Assert.True(PathIdentity.PathsEqual("/tmp/a/../a/b/", "/tmp/a/b/"));
    }

    [Fact]
    public void DifferentPaths_AreNotEqual()
    {
        Assert.False(PathIdentity.PathsEqual("/tmp/a", "/tmp/b"));
    }

    [Fact]
    public void WindowsPaths_CompareCaseInsensitive_OnWindows()
    {
        // 平台分支：Windows 上大小写不敏感，其余平台敏感（随平台跑对应分支，两语义都有断言）
        bool equal = PathIdentity.PathsEqual("/tmp/Data", "/tmp/data");
        Assert.Equal(OperatingSystem.IsWindows(), equal);
    }
}
