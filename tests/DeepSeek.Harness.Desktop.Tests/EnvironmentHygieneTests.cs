using System.Diagnostics;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>子进程环境净化单点（ADR spawn-env-and-plugin-spec-hardening）的判定与剥离语义。</summary>
public class EnvironmentHygieneTests
{
    /// <summary>验证 denylist 只命中 <c>NODE_OPTIONS</c>（精确）与 npm/pnpm/corepack 前缀（大小写不敏感），不误伤自有前缀与相似名。</summary>
    /// <param name="name">环境变量名。</param>
    /// <param name="expected">期望判定。</param>
    [Theory]
    [InlineData("NODE_OPTIONS", true)]
    [InlineData("node_options", true)]
    [InlineData("npm_config_registry", true)]
    [InlineData("NPM_CONFIG_REGISTRY", true)]
    [InlineData("pnpm_config_minimum_release_age", true)]
    [InlineData("PNPM_HOME", true)]
    [InlineData("Corepack_Home", true)]
    [InlineData("DSH_DESKTOP_SPAWN_TOKEN", false)]
    [InlineData("DSH_HOME", false)]
    [InlineData("PATH", false)]
    [InlineData("NODE_OPTIONS_EXTRA", false)]
    public void IsInheritedNoise_ClassifiesByDenylist(string name, bool expected) =>
        Assert.Equal(expected, EnvironmentHygiene.IsInheritedNoise(name));

    /// <summary>验证剥离只移除继承噪声，自有注入与无关变量原样保留。</summary>
    [Fact]
    public void StripInherited_RemovesNoise_KeepsOwnAndUnrelated()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["NODE_OPTIONS"] = "--require /evil.js";
        psi.Environment["npm_config_registry"] = "https://evil.example";
        psi.Environment["pnpm_config_store_dir"] = "/tmp/store";
        psi.Environment["corepack_home"] = "/tmp/corepack";
        psi.Environment["DSH_DESKTOP_SPAWN_TOKEN"] = "tok";
        psi.Environment["DSH_HOME"] = "/home/u/.dsh";
        psi.Environment["PATH"] = "/usr/bin";

        EnvironmentHygiene.StripInherited(psi);

        Assert.False(psi.Environment.ContainsKey("NODE_OPTIONS"));
        Assert.False(psi.Environment.ContainsKey("npm_config_registry"));
        Assert.False(psi.Environment.ContainsKey("pnpm_config_store_dir"));
        Assert.False(psi.Environment.ContainsKey("corepack_home"));
        Assert.Equal("tok", psi.Environment["DSH_DESKTOP_SPAWN_TOKEN"]);
        Assert.Equal("/home/u/.dsh", psi.Environment["DSH_HOME"]);
        Assert.Equal("/usr/bin", psi.Environment["PATH"]);
    }
}
