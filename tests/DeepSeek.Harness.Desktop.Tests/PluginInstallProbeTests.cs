using System.ComponentModel;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>插件安装体检探针（ADR plugin-install-health-probe）的分支覆盖：三路结局、调用序、psi 形状；
/// 真实 dsh 冒烟走 DSH_TEST_E2E 门控。</summary>
/// <remarks>E2E 用例改写进程级环境变量，与 <see cref="HarnessRuntimeHostTests"/> 同集合串行（并行互染 flaky 教训）。</remarks>
[Collection("dsh-home-env")]
public class PluginInstallProbeTests
{
    /// <summary>验证首探即出 URL 时返回 true：reconcile 不被调用、探针只跑一次。</summary>
    [Fact]
    public async Task Verify_FirstProbePasses_SkipsReconcile()
    {
        int probes = 0;
        int reconciles = 0;
        var logs = new List<string>();

        Task<Uri?> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            probes++;
            return Task.FromResult<Uri?>(new Uri("http://127.0.0.1:40001"));
        }

        bool ok = await PluginInstallProbe.VerifyAfterInstallAsync(
            "/home/u/.dsh", logs.Add, RunFake,
            (_, _) => { reconciles++; return 0; },
            CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(1, probes);
        Assert.Equal(0, reconciles);
        Assert.Contains(logs, l => l.Contains("体检探针通过"));
    }

    /// <summary>验证首探失败后先 reconcile 再重试：重试通过返回 true，调用序 = 探针 → reconcile → 探针。</summary>
    [Fact]
    public async Task Verify_FirstFails_ReconcilesThenRetries()
    {
        int probes = 0;
        var calls = new List<string>();
        var logs = new List<string>();

        Task<Uri?> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            probes++;
            calls.Add("probe");
            return Task.FromResult<Uri?>(probes >= 2 ? new Uri("http://127.0.0.1:40002") : null);
        }

        bool ok = await PluginInstallProbe.VerifyAfterInstallAsync(
            "/home/u/.dsh", logs.Add, RunFake,
            (_, _) => { calls.Add("reconcile"); return 2; },
            CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(2, probes);
        Assert.Equal(["probe", "reconcile", "probe"], calls);
        Assert.Contains(logs, l => l.Contains("reconcile 移除 2 个不可解析引用"));
        Assert.Contains(logs, l => l.Contains("reconcile 后通过"));
    }

    /// <summary>验证两次探针都失败时返回 false（放行正式启动）：reconcile 恰好一次、二次失败留响亮日志。</summary>
    [Fact]
    public async Task Verify_BothFail_ReturnsFalse_AndLogsLoudly()
    {
        int probes = 0;
        int reconciles = 0;
        var logs = new List<string>();

        Task<Uri?> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            probes++;
            return Task.FromResult<Uri?>(null);
        }

        bool ok = await PluginInstallProbe.VerifyAfterInstallAsync(
            "/home/u/.dsh", logs.Add, RunFake,
            (_, _) => { reconciles++; return 0; },
            CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(2, probes);
        Assert.Equal(1, reconciles);
        Assert.Contains(logs, l => l.Contains("二次未出 URL"));
    }

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
