using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>开发运行时隔离的纯判定：dev 识别、ApplicationId 后缀、隔离 home 推导。</summary>
public class DevEnvironmentTests
{
    [Theory]
    [InlineData(null, null, false)] // 打包新装（online-first 无闭包）：无任何标记 → 非 dev
    [InlineData(null, "", false)]
    [InlineData(null, "0", false)] // 仅显式 1 生效
    [InlineData(null, "1", true)]
    [InlineData("", "1", true)]
    [InlineData("/repo/resources/runtime", null, true)] // runtime 目录覆盖 = dev 意图
    [InlineData("  ", null, false)]
    public void IsDevRuntime_OnlyExplicitEnvMarkers(string? runtimeDir, string? devFlag, bool expected)
    {
        Assert.Equal(expected, DevEnvironment.IsDevRuntime(runtimeDir, devFlag));
    }

    [Theory]
    [InlineData("io.github.ZK-Andy.dotnet-deepseek-harness-desktop", true,
        "io.github.ZK-Andy.dotnet-deepseek-harness-desktop.dev")]
    [InlineData("io.github.ZK-Andy.dotnet-deepseek-harness-desktop", false,
        "io.github.ZK-Andy.dotnet-deepseek-harness-desktop")]
    public void ApplicationIdFor_SuffixesOnlyInDev(string baseId, bool isDev, string expected)
    {
        Assert.Equal(expected, DevEnvironment.ApplicationIdFor(baseId, isDev));
    }

    [Fact]
    public void DeriveDefaultDevHome_PrefersRuntimeDirTwoLevelsUp()
    {
        string runtimeDir = Path.Combine("/mnt/work/repo", "resources", "runtime");
        // runtime 目录形态优先，即使 baseDirectory 也可用
        Assert.Equal(Path.Combine("/mnt/work/repo", ".cache", "dev-home"),
            DevEnvironment.DeriveDefaultDevHome(runtimeDir, "/elsewhere/bin"));
    }

    [Fact]
    public void DeriveDefaultDevHome_WalksUpToGitRoot_FromBaseDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "devenv-" + Guid.NewGuid().ToString("N"));
        string binDir = Path.Combine(root, "src", "App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            Assert.Equal(Path.Combine(root, ".cache", "dev-home"),
                DevEnvironment.DeriveDefaultDevHome(null, binDir));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeriveDefaultDevHome_ReturnsNull_WhenNoGitAncestor()
    {
        string root = Path.Combine(Path.GetTempPath(), "devenv-" + Guid.NewGuid().ToString("N"));
        string binDir = Path.Combine(root, "bin");
        Directory.CreateDirectory(binDir);
        try
        {
            Assert.Null(DevEnvironment.DeriveDefaultDevHome(null, binDir));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeriveDefaultDevHome_ReturnsNull_WhenNoInputs()
    {
        Assert.Null(DevEnvironment.DeriveDefaultDevHome(null, null));
        Assert.Null(DevEnvironment.DeriveDefaultDevHome("", ""));
    }
}
