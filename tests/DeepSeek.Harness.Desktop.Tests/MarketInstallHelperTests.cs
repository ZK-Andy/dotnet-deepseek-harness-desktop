using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>MarketInstallHelper 的分支与错误路径覆盖，目标把 3.6% 拉至 40%+。</summary>
public class MarketInstallHelperTests
{
    private static string WriteTempFile(string content)
    {
        var p = Path.Combine(Path.GetTempPath(), "market-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(MarketInstallHelper.IsBundleInstalled(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dshmarket"));
    }

    [Fact]
    public void IsBundleInstalled_ReturnsTrue_WhenBothPresent()
    {
        var json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}
            """;
        var p = WriteTempFile(json);
        try { Assert.True(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenBundlesMissing()
    {
        var json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}
            """;
        var p = WriteTempFile(json);
        try { Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenDepsMissing()
    {
        var json = """
            {"dependencies":{},"dsh":{"profile":{"bundles":["dshmarket"]}}}
            """;
        var p = WriteTempFile(json);
        try { Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenInvalidJson()
    {
        var p = WriteTempFile("not json");
        try { Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsBundleInstalled_PerPackage_Independent()
    {
        // 市场已就位、伴生未装：市场判定 true、伴生判定 false（互不误判）
        var json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}
            """;
        var p = WriteTempFile(json);
        try
        {
            Assert.True(MarketInstallHelper.IsBundleInstalled(p, "dshmarket"));
            Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dsh-desktop-companion"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task CleanupBogusApp_RemovesOnlyAppWithTgz()
    {
        var json = """
            {"dependencies":{"app":"file:/tmp/dshmarket.tgz","keep":"1.0.0"},"dsh":{"profile":{"bundles":[]}}}
            """;
        var p = WriteTempFile(json);
        try
        {
            await MarketInstallHelper.CleanupBogusAppDependencyAsync(p);
            var after = File.ReadAllText(p);
            Assert.DoesNotContain("\"app\"", after);
            Assert.Contains("\"keep\"", after);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task CleanupBogusApp_NoOp_WhenNoApp()
    {
        var json = """{"dependencies":{"keep":"1.0.0"}}""";
        var p = WriteTempFile(json);
        try
        {
            await MarketInstallHelper.CleanupBogusAppDependencyAsync(p);
            Assert.Contains("keep", File.ReadAllText(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task CleanupBogusApp_NoOp_WhenFileMissing()
    {
        await MarketInstallHelper.CleanupBogusAppDependencyAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void EnsureWorkspaceAllowBuilds_AddsEsbuild_WhenMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "pnpm-workspace.yaml");
        Assert.False(File.Exists(path));
        try
        {
            MarketInstallHelper.EnsureWorkspaceAllowBuilds(path); // 缺失：不抛
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnsureWorkspaceAllowBuilds_NoOp_WhenMissing()
    {
        MarketInstallHelper.EnsureWorkspaceAllowBuilds(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        // 不抛即通过
    }

    [Fact]
    public void ResolveCompanionSpec_PrefersInstallerPluginsDir()
    {
        var plugins = Path.Combine(Path.GetTempPath(), "pl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plugins);
        var packagedTgz = Path.Combine(plugins, "dsh-desktop-companion.tgz");
        File.WriteAllBytes(packagedTgz, new byte[2 * 1024]);
        try
        {
            // 安装器资源是打包形态唯一供给源（运行时目录种子已退役）
            Assert.Equal(packagedTgz, MarketInstallHelper.ResolveCompanionSpec(plugins));
        }
        finally { Directory.Delete(plugins, true); }
    }

    [Fact]
    public void ResolveCompanionSpec_ReturnsNull_WhenNothing()
    {
        // 伴生插件无 registry 回退：安装器资源缺失时返回 null（调用方跳过）。
        // 运行时目录种子已退役，不提供 tgz/目录回退。
        Assert.Null(MarketInstallHelper.ResolveCompanionSpec(null));
    }

    [Fact]
    public async Task EnsureBundles_AddsWhenMissing()
    {
        var json = """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""";
        var p = WriteTempFile(json);
        try
        {
            var added = await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dshmarket");
            Assert.True(added);
            Assert.Contains("dshmarket", File.ReadAllText(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task EnsureBundles_AppendsSecondPackage_KeepsFirst()
    {
        var json = """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}""";
        var p = WriteTempFile(json);
        try
        {
            var added = await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dsh-desktop-companion");
            Assert.True(added);
            var after = File.ReadAllText(p);
            Assert.Contains("dshmarket", after);
            Assert.Contains("dsh-desktop-companion", after);
            // 再补写一次应幂等
            Assert.False(await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dsh-desktop-companion"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task EnsureBundles_NoOpWhenPresent()
    {
        var json = """{"dsh":{"profile":{"bundles":["dshmarket"]}}}""";
        var p = WriteTempFile(json);
        try
        {
            var added = await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dshmarket");
            Assert.False(added);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task EnsureBundles_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(await MarketInstallHelper.EnsureBundlesContainsAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dshmarket"));
    }

    [Fact]
    public async Task EnsureMarketFromRegistry_BuildsCorrectArgsAndEnv_AndBackfillsBundles()
    {
        var home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName));
        var workspace = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName, "pnpm-workspace.yaml");
        var profilePkg = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName, "package.json");
        // profile 清单：bundles 尚缺 dshmarket → 装后应补写。
        File.WriteAllText(profilePkg, """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");
        // workspace 尚缺 allowBuilds → helper 应先放行。
        File.WriteAllText(workspace, "packages:\n  - .\n");

        System.Diagnostics.ProcessStartInfo? capturedPsi = null;
        var logs = new List<string>();
        var args = new List<string>();

        async Task<(int Exit, string Out, string Err)> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            capturedPsi = psi;
            args.Clear();
            foreach (var a in psi.ArgumentList) args.Add(a);
            return (0, "installed", string.Empty);
        }

        try
        {
            await MarketInstallHelper.EnsureMarketFromRegistryAsync(
                "node", "/dsh/bin.js", home, logs.Add, RunFake, CancellationToken.None);

            Assert.NotNull(capturedPsi);
            // 参数形状：dsh bin.js plugin --profile desktop add dshmarket@latest
            Assert.Equal(["/dsh/bin.js", "plugin", "--profile", HarnessRuntimeHost.DesktopProfileName, "add", MarketInstallHelper.MarketSpec], args);
            Assert.Equal(home, capturedPsi!.Environment["DSH_HOME"]);
            Assert.Equal(Path.Combine(home, ".pnpm-store"), capturedPsi.Environment["pnpm_config_store_dir"]);
            // allowBuilds 已放行（ESBuild 至少一条）
            Assert.Contains("allowBuilds", File.ReadAllText(workspace));
            // bundles 已补写
            Assert.Contains("dshmarket", File.ReadAllText(profilePkg));
            Assert.Contains(logs, l => l.Contains("已补写 bundles dshmarket"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureMarketFromRegistry_RetriesOnMinimumReleaseAge_Dropcap()
    {
        var home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName));
        var profilePkg = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName, "package.json");
        File.WriteAllText(profilePkg, """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");

        var relaxSeen = false;
        var runs = 0;
        var logs = new List<string>();

        async Task<(int Exit, string Out, string Err)> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            runs++;
            // 首次发现 minimumReleaseAge 政策拒绝；放宽后第二次应带 pnpm_config_minimum_release_age=0。
            if (runs == 1)
            {
                return (1, "ERR_PNPM_POLICY_MINIMUM_RELEASE_AGE", string.Empty);
            }

            relaxSeen = psi.Environment.ContainsKey("pnpm_config_minimum_release_age");
            return (0, "installed", string.Empty);
        }

        try
        {
            await MarketInstallHelper.EnsureMarketFromRegistryAsync(
                "node", "/dsh/bin.js", home, logs.Add, RunFake, CancellationToken.None);

            Assert.Equal(2, runs);
            Assert.True(relaxSeen, "放宽重试应带 pnpm_config_minimum_release_age=0");
            Assert.Contains(logs, l => l.Contains("minimumReleaseAge 政策拒绝"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_InstallsCompanion_AndBackfillsBundles()
    {
        var home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        var profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        var profilePkg = Path.Combine(profileDir, "package.json");
        var installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        // ResolveCompanionSpec 要求 >1K 的 tgz（防 0.1.10 假包）；profile 不含 companion → AssemblePending 返回待装。
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        File.WriteAllText(profilePkg, """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");

        System.Diagnostics.ProcessStartInfo? capturedPsi = null;
        var args = new List<string>();
        var logs = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            capturedPsi = psi;
            args.Clear();
            foreach (var a in psi.ArgumentList) args.Add(a);
            return (0, "installed", string.Empty);
        }

        try
        {
            await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFake, CancellationToken.None);

            Assert.NotNull(capturedPsi);
            // 参数形状：dsh bin.js plugin --profile desktop add <companion tgz>
            Assert.Equal(
                ["/dsh/bin.js", "plugin", "--profile", HarnessRuntimeHost.DesktopProfileName, "add", Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz")],
                args);
            Assert.Equal(home, capturedPsi!.Environment["DSH_HOME"]);
            // companion 已补写 bundles
            Assert.Contains("dsh-desktop-companion", File.ReadAllText(profilePkg));
            Assert.Contains(logs, l => l.Contains("已补写 bundles dsh-desktop-companion"));
            // 桌面核心不变量：web-app 层缺失时被补回
            Assert.Contains("@deepseek-ai/dsh-web-app", File.ReadAllText(profilePkg));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_NoRun_WhenCompanionInstalled()
    {
        var home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        var profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        var profilePkg = Path.Combine(profileDir, "package.json");
        var installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        // companion 已就位（dependencies + bundles 双含）→ AssemblePending 返回空 → 不 spawn。
        File.WriteAllText(profilePkg, """{"dependencies":{"dsh-desktop-companion":"file:./plugins/dsh-desktop-companion.tgz"},"dsh":{"profile":{"bundles":["dsh-desktop-companion","@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app"]}}}""");

        var runs = 0;
        var logs = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            runs++;
            return (0, "installed", string.Empty);
        }

        try
        {
            await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFake, CancellationToken.None);
            Assert.Equal(0, runs);
            Assert.Contains(logs, l => l.Contains("无需安装"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_RetriesOnMinimumReleaseAge_Dropcap()
    {
        var home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        var profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        var profilePkg = Path.Combine(profileDir, "package.json");
        var installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        File.WriteAllText(profilePkg, """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");

        var relaxSeen = false;
        var runs = 0;
        var logs = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            runs++;
            if (runs == 1)
            {
                return (1, "ERR_PNPM_POLICY_MINIMUM_RELEASE_AGE", string.Empty);
            }

            relaxSeen = psi.Environment.ContainsKey("pnpm_config_minimum_release_age");
            return (0, "installed", string.Empty);
        }

        try
        {
            await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFake, CancellationToken.None);
            Assert.Equal(2, runs);
            Assert.True(relaxSeen, "放宽重试应带 pnpm_config_minimum_release_age=0");
            Assert.Contains(logs, l => l.Contains("minimumReleaseAge 政策拒绝"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_PathDsh_UsesDshCommand_WhenNoBundledRuntime()
    {
        // v0.4.0 实机回归：PATH-dsh 运行时（无捆绑闭包、无引导）nodeExe/dshEntry 双 null →
        // 用 PATH 上的 dsh 命令（等价宿主 spawn），无 bin.js 入口参数。companion 仍须安装并补写 bundles。
        var home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        var profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        var profilePkg = Path.Combine(profileDir, "package.json");
        var installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        File.WriteAllText(profilePkg, """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");

        System.Diagnostics.ProcessStartInfo? capturedPsi = null;
        var args = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            capturedPsi = psi;
            args.Clear();
            foreach (var a in psi.ArgumentList) args.Add(a);
            return (0, "installed", string.Empty);
        }

        try
        {
            await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                null, null, home, installerPluginsDir, _ => { }, RunFake, CancellationToken.None);

            Assert.NotNull(capturedPsi);
            Assert.Equal("dsh", capturedPsi!.FileName);
            // 参数形状：dsh plugin --profile desktop add <companion tgz>（无 bin.js 入口）
            Assert.Equal(
                ["plugin", "--profile", HarnessRuntimeHost.DesktopProfileName, "add", Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz")],
                args);
            Assert.Contains("dsh-desktop-companion", File.ReadAllText(profilePkg));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
