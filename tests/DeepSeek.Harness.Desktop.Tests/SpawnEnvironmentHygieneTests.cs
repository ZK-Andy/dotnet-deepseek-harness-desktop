using System.Diagnostics;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>四处 spawn 点的环境净化接线（ADR spawn-env-and-plugin-spec-hardening）：每个 builder 都须
/// 先剥离宿主噪声、再写自有注入。</summary>
/// <remarks>改写进程级环境变量，必须与 dsh-home 环境型测试同集合串行（见
/// <see cref="DshHomeEnvCollectionDefinition"/>）。</remarks>
[Collection("dsh-home-env")]
public class SpawnEnvironmentHygieneTests
{
    /// <summary>噪声变量 + 一个仅用于证明「继承确实生效」的标记变量。</summary>
    private static readonly Dictionary<string, string?> s_noise = new()
    {
        ["NODE_OPTIONS"] = "--require /evil.js",
        ["npm_config_registry"] = "https://evil.example",
        ["pnpm_config_minimum_release_age"] = "0",
        ["corepack_home"] = "/tmp/corepack",
        ["DSH_TEST_INHERIT_MARKER"] = "inherited",
    };

    /// <summary>在噪声环境变量生效期间执行断言体，结束后逐名还原。</summary>
    /// <param name="action">断言体。</param>
    private static async Task WithNoiseAsync(Func<Task> action)
    {
        var saved = new Dictionary<string, string?>();
        foreach ((string name, string? value) in s_noise)
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            await action();
        }
        finally
        {
            foreach ((string name, string? value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>标记变量在子进程环境里仍可见——证明 builder 读到的确实是继承环境，噪声缺席才不是空证。</summary>
    /// <param name="psi">待断言的启动信息。</param>
    private static void AssertInheritanceProof(ProcessStartInfo psi)
    {
        Assert.Equal("inherited", psi.Environment["DSH_TEST_INHERIT_MARKER"]);
        Assert.False(psi.Environment.ContainsKey("NODE_OPTIONS"));
        Assert.False(psi.Environment.ContainsKey("npm_config_registry"));
        Assert.False(psi.Environment.ContainsKey("pnpm_config_minimum_release_age"));
        Assert.False(psi.Environment.ContainsKey("corepack_home"));
    }

    /// <summary>验证 dsh web 宿主 spawn 剥离继承噪声，且自有 DSH_HOME/token 注入不受影响。</summary>
    [Fact]
    public async Task BuildStartPsi_StripsNoise_KeepsOwnInjection() => await WithNoiseAsync(() =>
    {
        using var host = new HarnessRuntimeHost();
        ProcessStartInfo psi = host.BuildStartPsi(0, "/home/u/.dsh", "tok");

        AssertInheritanceProof(psi);
        Assert.Equal("/home/u/.dsh", psi.Environment["DSH_HOME"]);
        Assert.Equal("tok", psi.Environment["DSH_DESKTOP_SPAWN_TOKEN"]);
        Assert.Equal("dsh", psi.FileName);
        return Task.CompletedTask;
    });

    /// <summary>验证版本探针 spawn 剥离继承噪声（否则宿主 NODE_OPTIONS 会在探针期执行任意代码）。</summary>
    [Fact]
    public async Task BuildProbePsi_StripsNoise() => await WithNoiseAsync(() =>
    {
        ProcessStartInfo psi = RuntimeVersionGate.BuildProbePsi();

        AssertInheritanceProof(psi);
        Assert.Equal("dsh", psi.FileName);
        Assert.Equal(["--version"], psi.ArgumentList);
        return Task.CompletedTask;
    });

    /// <summary>验证引导捕获 spawn（npm 全局安装）剥离继承噪声，并把 npm-cli 的 NODE_OPTIONS 换成我方堆上限。</summary>
    [Fact]
    public async Task BuildCapturePsi_NpmCli_ReplacesNodeOptions() => await WithNoiseAsync(() =>
    {
        ProcessStartInfo psi = RuntimeBootstrap.BuildCapturePsi(
            "node", ["/usr/lib/node_modules/npm/bin/npm-cli.js", "install", "-g", "@deepseek-ai/dsh@alpha"]);

        Assert.Equal("inherited", psi.Environment["DSH_TEST_INHERIT_MARKER"]);
        Assert.False(psi.Environment.ContainsKey("npm_config_registry"));
        Assert.False(psi.Environment.ContainsKey("pnpm_config_minimum_release_age"));
        Assert.False(psi.Environment.ContainsKey("corepack_home"));
        Assert.Equal("--max-old-space-size=3072", psi.Environment["NODE_OPTIONS"]);
        return Task.CompletedTask;
    });

    /// <summary>验证非 npm 的引导捕获 spawn 不注入 NODE_OPTIONS（继承噪声已被剥离）。</summary>
    [Fact]
    public async Task BuildCapturePsi_NonNpm_LeavesNoNodeOptions() => await WithNoiseAsync(() =>
    {
        ProcessStartInfo psi = RuntimeBootstrap.BuildCapturePsi("node", ["-e", "console.log(process.execPath)"]);

        AssertInheritanceProof(psi);
        return Task.CompletedTask;
    });

    /// <summary>验证 dsh plugin add 的 spawn 也走同一净化（安装链路的 registry 改写同样被挡）。</summary>
    [Fact]
    public async Task RunPluginAddAsync_StripsNoise() => await WithNoiseAsync(async () =>
    {
        ProcessStartInfo? captured = null;
        Task<(int Exit, string Out, string Err)> Fake(ProcessStartInfo psi, CancellationToken ct)
        {
            captured = psi;
            return Task.FromResult((0, "installed", string.Empty));
        }

        (int exit, string _, string _) = await MarketInstallHelper.RunPluginAddAsync(
            "node",
            "/dsh/bin.js",
            "/home/u/.dsh",
            "dshmarket@latest",
            MarketInstallHelper.PluginSpecOrigin.Registry,
            _ => { },
            Fake,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.NotNull(captured);
        AssertInheritanceProof(captured!);
        Assert.Equal("/home/u/.dsh", captured!.Environment["DSH_HOME"]);
    });
}
