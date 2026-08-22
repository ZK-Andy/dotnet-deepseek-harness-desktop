using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>开发运行时隔离的纯判定：dev 识别、ApplicationId 后缀、隔离 home 推导。</summary>
public class DevEnvironmentTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("/repo/resources/runtime", true)]
    public void IsDevRuntime_OnlyEnvPresenceMatters(string? runtimeDir, bool expected)
    {
        Assert.Equal(expected, DevEnvironment.IsDevRuntime(runtimeDir));
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
    public void DeriveDefaultDevHome_WalksTwoLevelsUp()
    {
        var runtimeDir = Path.Combine("/mnt/work/repo", "resources", "runtime");
        Assert.Equal(Path.Combine("/mnt/work/repo", ".cache", "dev-home"),
            DevEnvironment.DeriveDefaultDevHome(runtimeDir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DeriveDefaultDevHome_ReturnsNull_WhenNoRuntimeDir(string? runtimeDir)
    {
        Assert.Null(DevEnvironment.DeriveDefaultDevHome(runtimeDir));
    }
}
