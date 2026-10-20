using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>应用重启派生自身：启动形态与原始参数透传（纯函数面，不 spawn 进程）。</summary>
public class AppRelaunchTests
{
    /// <summary>验证派生自身：FileName 为可执行文件、<c>UseShellExecute=false</c>、原始参数原样透传。</summary>
    [Fact]
    public void BuildStartInfo_KeepsSpawnShape_AndForwardsArgs()
    {
        ProcessStartInfo psi = AppRelaunch.BuildStartInfo("/opt/dsh/desktop", ["--dev", "--profile x"]);

        Assert.Equal("/opt/dsh/desktop", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(["--dev", "--profile x"], psi.ArgumentList.ToArray());
    }

    /// <summary>验证无参数时启动信息仍带可执行文件与禁用 shell 执行的形态（空参数不产出空参数项）。</summary>
    [Fact]
    public void BuildStartInfo_NoArgs_KeepsFileNameAndEmptyArgs()
    {
        ProcessStartInfo psi = AppRelaunch.BuildStartInfo("/opt/dsh/desktop", []);

        Assert.Equal("/opt/dsh/desktop", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Empty(psi.ArgumentList);
    }
}
