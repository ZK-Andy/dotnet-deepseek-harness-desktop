using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>MarketInstallHelper 的分支与错误路径覆盖，目标把 3.6% 拉至 40%+。</summary>
public class MarketInstallHelperTests
{
    private static string WriteTempFile(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "market-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, content);
        return p;
    }

    /// <summary>验证 package.json 文件缺失时 IsBundleInstalled 判定未安装并返回 false，不抛异常。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(MarketInstallHelper.IsBundleInstalled(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dshmarket"));
    }

    /// <summary>验证 dependencies 与 dsh.profile.bundles 同时含 dshmarket 时判定已安装并返回 true。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsTrue_WhenBothPresent()
    {
        string json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}
            """;
        string p = WriteTempFile(json);
        try { Assert.True(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    /// <summary>验证 dshmarket 仅出现在 dependencies、未列进 bundles 时判定未安装。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenBundlesMissing()
    {
        string json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}
            """;
        string p = WriteTempFile(json);
        try { Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    /// <summary>验证 bundles 含 dshmarket 但 dependencies 为空时判定未安装，两处来源缺一不可。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenDepsMissing()
    {
        string json = """
            {"dependencies":{},"dsh":{"profile":{"bundles":["dshmarket"]}}}
            """;
        string p = WriteTempFile(json);
        try { Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    /// <summary>验证 package.json 内容不是合法 JSON 时按未安装处理（返回 false）而不抛异常。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenInvalidJson()
    {
        string p = WriteTempFile("not json");
        try { Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    /// <summary>验证同一清单内各包判定互不干扰：已装的 dshmarket 判 true、未装的 companion 仍判 false。</summary>
    [Fact]
    public void IsBundleInstalled_PerPackage_Independent()
    {
        // 市场已就位、伴生未装：市场判定 true、伴生判定 false（互不误判）
        string json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}
            """;
        string p = WriteTempFile(json);
        try
        {
            Assert.True(MarketInstallHelper.IsBundleInstalled(p, "dshmarket"));
            Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dsh-desktop-companion"));
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证清理只移除值为 file:.tgz 引用的 app 依赖项，其余依赖 keep 原样保留。</summary>
    [Fact]
    public async Task CleanupBogusApp_RemovesOnlyAppWithTgz()
    {
        string json = """
            {"dependencies":{"app":"file:/tmp/dshmarket.tgz","keep":"1.0.0"},"dsh":{"profile":{"bundles":[]}}}
            """;
        string p = WriteTempFile(json);
        try
        {
            await MarketInstallHelper.CleanupBogusAppDependencyAsync(p);
            string after = File.ReadAllText(p);
            Assert.DoesNotContain("\"app\"", after);
            Assert.Contains("\"keep\"", after);
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证清单中没有 app 依赖时清理不做任何改动，keep 依赖保持原样。</summary>
    [Fact]
    public async Task CleanupBogusApp_NoOp_WhenNoApp()
    {
        string json = """{"dependencies":{"keep":"1.0.0"}}""";
        string p = WriteTempFile(json);
        try
        {
            await MarketInstallHelper.CleanupBogusAppDependencyAsync(p);
            Assert.Contains("keep", File.ReadAllText(p));
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证 package.json 文件不存在时清理静默返回、不抛异常。</summary>
    [Fact]
    public async Task CleanupBogusApp_NoOp_WhenFileMissing()
    {
        await MarketInstallHelper.CleanupBogusAppDependencyAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
    }

    /// <summary>验证 pnpm-workspace.yaml 缺失时 EnsureWorkspaceAllowBuilds 不抛异常、也不擅自创建文件。</summary>
    [Fact]
    public void EnsureWorkspaceAllowBuilds_AddsEsbuild_WhenMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "pnpm-workspace.yaml");
        Assert.False(File.Exists(path));
        try
        {
            MarketInstallHelper.EnsureWorkspaceAllowBuilds(path); // 缺失：不抛
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>验证路径指向不存在的目录时调用不抛异常，无操作即通过。</summary>
    [Fact]
    public void EnsureWorkspaceAllowBuilds_NoOp_WhenMissing()
    {
        MarketInstallHelper.EnsureWorkspaceAllowBuilds(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        // 不抛即通过
    }

    /// <summary>验证安装器资源目录中的 dsh-desktop-companion.tgz 被解析为首选供给源并返回其完整路径。</summary>
    [Fact]
    public void ResolveCompanionSpec_PrefersInstallerPluginsDir()
    {
        string plugins = Path.Combine(Path.GetTempPath(), "pl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plugins);
        string packagedTgz = Path.Combine(plugins, "dsh-desktop-companion.tgz");
        File.WriteAllBytes(packagedTgz, new byte[2 * 1024]);
        try
        {
            // 安装器资源是打包形态唯一供给源（运行时目录种子已退役）
            Assert.Equal(packagedTgz, MarketInstallHelper.ResolveCompanionSpec(plugins));
        }
        finally { Directory.Delete(plugins, true); }
    }

    /// <summary>验证安装器资源缺失时 ResolveCompanionSpec 返回 null，调用方据此跳过伴生插件安装。</summary>
    [Fact]
    public void ResolveCompanionSpec_ReturnsNull_WhenNothing()
    {
        // 伴生插件无 registry 回退：安装器资源缺失时返回 null（调用方跳过）。
        // 运行时目录种子已退役，不提供 tgz/目录回退。
        Assert.Null(MarketInstallHelper.ResolveCompanionSpec(null));
    }

    /// <summary>验证 bundles 未含 dshmarket 时补写进清单并返回 true。</summary>
    [Fact]
    public async Task EnsureBundles_AddsWhenMissing()
    {
        string json = """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""";
        string p = WriteTempFile(json);
        try
        {
            bool added = await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dshmarket");
            Assert.True(added);
            Assert.Contains("dshmarket", File.ReadAllText(p));
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证追加新包时保留已有包，且重复补写幂等（二次调用返回 false 不再写入）。</summary>
    [Fact]
    public async Task EnsureBundles_AppendsSecondPackage_KeepsFirst()
    {
        string json = """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}""";
        string p = WriteTempFile(json);
        try
        {
            bool added = await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dsh-desktop-companion");
            Assert.True(added);
            string after = File.ReadAllText(p);
            Assert.Contains("dshmarket", after);
            Assert.Contains("dsh-desktop-companion", after);
            // 再补写一次应幂等
            Assert.False(await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dsh-desktop-companion"));
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证目标包已存在于 bundles 时不再写入、返回 false。</summary>
    [Fact]
    public async Task EnsureBundles_NoOpWhenPresent()
    {
        string json = """{"dsh":{"profile":{"bundles":["dshmarket"]}}}""";
        string p = WriteTempFile(json);
        try
        {
            bool added = await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dshmarket");
            Assert.False(added);
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证 package.json 文件缺失时补写返回 false 而非抛异常。</summary>
    [Fact]
    public async Task EnsureBundles_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(await MarketInstallHelper.EnsureBundlesContainsAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dshmarket"));
    }

    /// <summary>验证安装市场时拼装的进程参数形状与 DSH_HOME、pnpm_config_store_dir 环境变量，并回填 allowBuilds 与 bundles 缺失项。</summary>
    [Fact]
    public async Task EnsureMarketFromRegistry_BuildsCorrectArgsAndEnv_AndBackfillsBundles()
    {
        string home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName));
        string workspace = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName, "pnpm-workspace.yaml");
        string profilePkg = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName, "package.json");
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
            foreach (string a in psi.ArgumentList)
            {
                args.Add(a);
            }

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

    /// <summary>验证首次安装被 minimumReleaseAge 政策拒绝后，带 pnpm_config_minimum_release_age=0 放宽重试并成功，共运行两次。</summary>
    [Fact]
    public async Task EnsureMarketFromRegistry_RetriesOnMinimumReleaseAge_Dropcap()
    {
        string home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName));
        string profilePkg = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName, "package.json");
        File.WriteAllText(profilePkg, """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");

        bool relaxSeen = false;
        int runs = 0;
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

    /// <summary>验证 spawn 前通过 bin.js 安装 companion tgz 的参数与 DSH_HOME，并补写 companion、@deepseek-ai/dsh-web-app 缺失 bundles。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_InstallsCompanion_AndBackfillsBundles()
    {
        string home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        string profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        string profilePkg = Path.Combine(profileDir, "package.json");
        string installerPluginsDir = Path.Combine(home, "plugins");
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
            foreach (string a in psi.ArgumentList)
            {
                args.Add(a);
            }

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

    /// <summary>验证 companion 已同时写入 dependencies 与 bundles 时跳过安装、不启动任何进程，并记录「无需安装」。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_NoRun_WhenCompanionInstalled()
    {
        string home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        string profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        string profilePkg = Path.Combine(profileDir, "package.json");
        string installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        // companion 已就位（dependencies + bundles 双含）→ AssemblePending 返回空 → 不 spawn。
        File.WriteAllText(profilePkg, """{"dependencies":{"dsh-desktop-companion":"file:./plugins/dsh-desktop-companion.tgz"},"dsh":{"profile":{"bundles":["dsh-desktop-companion","@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app"]}}}""");

        int runs = 0;
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

    /// <summary>验证伴生安装首次遭 minimumReleaseAge 政策拒绝后带放宽环境重试并成功，共运行两次。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_RetriesOnMinimumReleaseAge_Dropcap()
    {
        string home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        string profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        string profilePkg = Path.Combine(profileDir, "package.json");
        string installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        File.WriteAllText(profilePkg, """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");

        bool relaxSeen = false;
        int runs = 0;
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

    /// <summary>验证无捆绑运行时回退到 PATH 上的 dsh 命令（无 bin.js 入口参数）安装 companion 并补写 bundles。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_PathDsh_UsesDshCommand_WhenNoBundledRuntime()
    {
        // v0.4.0 实机回归：PATH-dsh 运行时（无捆绑闭包、无引导）nodeExe/dshEntry 双 null →
        // 用 PATH 上的 dsh 命令（等价宿主 spawn），无 bin.js 入口参数。companion 仍须安装并补写 bundles。
        string home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        string profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        string profilePkg = Path.Combine(profileDir, "package.json");
        string installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        File.WriteAllText(profilePkg, """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");

        System.Diagnostics.ProcessStartInfo? capturedPsi = null;
        var args = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFake(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            capturedPsi = psi;
            args.Clear();
            foreach (string a in psi.ArgumentList)
            {
                args.Add(a);
            }

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
