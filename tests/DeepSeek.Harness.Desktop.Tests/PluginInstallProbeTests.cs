using System.ComponentModel;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>插件安装体检探针（ADR plugin-install-health-probe）的 psi 形状覆盖（含事务管线的 staging 指向）；
/// 真实 dsh 冒烟走 DSH_TEST_E2E 门控。行为分支（探针不过 → 放弃激活）由
/// <see cref="PluginProfileTransactionTests"/> 与安装驱动测试覆盖。</summary>
/// <remarks>E2E 用例改写进程级环境变量，与 <see cref="HarnessRuntimeHostTests"/> 同集合串行（并行互染 flaky 教训）。</remarks>
[Collection("dsh-home-env")]
public class PluginInstallProbeTests
{
    /// <summary>验证探针 psi 形状：dsh 命令、--port 0 --no-open、DSH_HOME、血统 token 与 CreateNoWindow——
    /// 与主 spawn 同一事实源（BuildStartPsi），端口让 OS 分配避开首选端口交接面。</summary>
    [Fact]
    public void BuildProbePsi_SharesSpawnShape_WithMainStart()
    {
        System.Diagnostics.ProcessStartInfo psi = PluginInstallProbe.BuildProbePsi("/home/u/.dsh");

        Assert.Equal("dsh", psi.FileName);
        string[] args = [.. psi.ArgumentList];
        Assert.Equal(
            ["--profile", HarnessRuntimeHost.DesktopProfileName, "--port", "0", "--no-open"],
            args);
        Assert.Equal("/home/u/.dsh", psi.Environment["DSH_HOME"]);
        Assert.False(string.IsNullOrWhiteSpace(psi.Environment[RuntimeLineage.TokenEnv]));
        Assert.True(psi.CreateNoWindow);
    }

    /// <summary>验证 homeOverride 指向 staging home 时探针 DSH_HOME 改指 staging（staged 体检的换入前验证面），
    /// 其余 spawn 形状（参数、token、CreateNoWindow）不变。</summary>
    [Fact]
    public void BuildProbePsi_HomeOverride_PointsAtStagingHome()
    {
        System.Diagnostics.ProcessStartInfo psi = PluginInstallProbe.BuildProbePsi("/home/u/.dsh", "/home/u/.dsh/.tx-abc");

        Assert.Equal("dsh", psi.FileName);
        string[] args = [.. psi.ArgumentList];
        Assert.Equal(
            ["--profile", HarnessRuntimeHost.DesktopProfileName, "--port", "0", "--no-open"],
            args);
        Assert.Equal("/home/u/.dsh/.tx-abc", psi.Environment["DSH_HOME"]);
        Assert.False(string.IsNullOrWhiteSpace(psi.Environment[RuntimeLineage.TokenEnv]));
        Assert.True(psi.CreateNoWindow);
    }

    /// <summary>DSH_TEST_E2E=1 且 PATH 中有 dsh 时真实 spawn 探针 dsh：断言隔离 home 上出 URL，
    /// 且探针进程被回收（PluginProcessRunner.RunProbeAsync 的击杀路径）。</summary>
    [Fact]
    public async Task RunProbeAsync_RealDsh_YieldsUrlAndReaps_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            // 未启用——保持绿色
            return;
        }

        string home = Path.Combine(Path.GetTempPath(), "dsh-probe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");
        try
        {
            // 新 home 无 profile 时 dsh 立即退出（拿不到 URL）——先做 profile 引导（入 try 保证环境清理）
            Assert.True(DesktopProfileBootstrap.EnsureProfile(home));
            Uri? url;
            try
            {
                url = await PluginProcessRunner.RunProbeAsync(PluginInstallProbe.BuildProbePsi(home), CancellationToken.None);
            }
            catch (Win32Exception)
            {
                // PATH 里没有 dsh——跳过
                return;
            }

            Assert.NotNull(url);
            Assert.StartsWith("http://127.0.0.1:", url!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }
}
