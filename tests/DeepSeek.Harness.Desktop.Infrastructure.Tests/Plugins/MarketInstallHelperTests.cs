
namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Plugins;

/// <summary>MarketInstallHelper 的分支与错误路径覆盖，目标把 3.6% 拉至 40%+。</summary>
public class MarketInstallHelperTests
{
    private static string WriteTempFile(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "market-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, content);
        return p;
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

    /// <summary>探针 fake：恒出 URL（staged 体检通过面）。</summary>
    private static Task<Uri?> ProbeOkAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        => Task.FromResult<Uri?>(new Uri("http://127.0.0.1:40010"));

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
        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
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
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeOkAsync, CancellationToken.None);

            Assert.True(installed);
            Assert.NotNull(capturedPsi);
            // 参数形状：dsh bin.js plugin --profile <DesktopProfileName> add <companion tgz>
            Assert.Equal(
                ["/dsh/bin.js", "plugin", "--profile", HarnessRuntimeHost.DesktopProfileName, "add", Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz")],
                args);
            // DSH_HOME 指向 staging home（事务管线）
            string stagingHome = capturedPsi!.Environment["DSH_HOME"]!;
            Assert.StartsWith(home, stagingHome);
            Assert.Contains(".tx-", stagingHome);
            // 换入后 active 清单已含 companion 与 web-app（staged 补写随换入生效）
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
        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            runs++;
            return (0, "installed", string.Empty);
        }

        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeOkAsync, CancellationToken.None);
            Assert.False(installed);
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
        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            if (IsPnpmPreflight(psi))
            {
                return (0, "10.0.0", string.Empty);   // 前置：PATH 已有 pnpm
            }

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
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeOkAsync, CancellationToken.None);
            Assert.True(installed);
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
        Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            capturedPsi = psi;
            return Task.FromResult((0, "installed", string.Empty));
        }

        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                null, null, home, installerPluginsDir, _ => { }, RunFakeAsync, ProbeOkAsync, CancellationToken.None);

            Assert.True(installed);
            Assert.NotNull(capturedPsi);
            // 命令形状：dsh plugin --profile <DesktopProfileName> add <companion tgz>（无 bin.js 入口；
            // Windows 经 cmd.exe 中转，见 DshSpawnAssert）
            DshSpawnAssert.ExeAndArgs(
                capturedPsi!,
                "plugin",
                "--profile",
                HarnessRuntimeHost.DesktopProfileName,
                "add",
                Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"));
            Assert.Contains("dsh-desktop-companion", File.ReadAllText(profilePkg));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证随包驱动 staged 探针未出 URL 时放弃激活：active 清单原封不动、staging/journal 已收口。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_DiscardsStaging_WhenProbeFails()
    {
        string home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        string profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        string profilePkg = Path.Combine(profileDir, "package.json");
        string original = """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""";
        File.WriteAllText(profilePkg, original);
        string installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        var logs = new List<string>();

        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
            => (0, "installed", string.Empty);
        Task<Uri?> ProbeDeadAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
            => Task.FromResult<Uri?>(null);

        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeDeadAsync, CancellationToken.None);
            Assert.False(installed);
            Assert.Equal(original, File.ReadAllText(profilePkg));
            Assert.Empty(Directory.GetDirectories(home, ".tx-*"));
            Assert.False(File.Exists(Path.Combine(home, "profiles", ".pending.json")));
            Assert.Contains(logs, l => l.Contains("放弃激活"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证随包驱动全部安装失败时 staging 作废：active 清单原封不动、无残留（全失败 ≠ 半变更）。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_AllInstallFail_DiscardsStaging()
    {
        string home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        string profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        string profilePkg = Path.Combine(profileDir, "package.json");
        string original = """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""";
        File.WriteAllText(profilePkg, original);
        string installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        var logs = new List<string>();

        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
            => IsPnpmPreflight(psi) && psi.FileName == "pnpm"
                ? (0, "10.0.0", string.Empty)   // 前置：PATH 已有 pnpm；plugin add 全失败
                : (1, string.Empty, "boom");

        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeOkAsync, CancellationToken.None);
            Assert.False(installed);
            Assert.Equal(original, File.ReadAllText(profilePkg));
            Assert.Empty(Directory.GetDirectories(home, ".tx-*"));
            Assert.False(File.Exists(Path.Combine(home, "profiles", ".pending.json")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>前置命令判定：EnsurePnpm 的探测/安装（BuildToolPsi 裸名形态；测试环境 Unix 直跑
    /// 裸名，Windows 的 cmd.exe 中转形态不在本测试矩阵内）。</summary>
    private static bool IsPnpmPreflight(System.Diagnostics.ProcessStartInfo psi)
        => psi.FileName is "pnpm" or "npm";

    /// <summary>构造一个「待装 companion」夹具 home（tgz 资源齐、profile 不含 companion → 前置必跑）。</summary>
    private static (string Home, string ProfilePkg, string InstallerPluginsDir) NewPendingCompanionHome()
    {
        string home = Path.Combine(Path.GetTempPath(), "mh-" + Guid.NewGuid().ToString("N"));
        string profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(profileDir);
        string profilePkg = Path.Combine(profileDir, "package.json");
        string installerPluginsDir = Path.Combine(home, "plugins");
        Directory.CreateDirectory(installerPluginsDir);
        File.WriteAllBytes(Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz"), new byte[2048]);
        File.WriteAllText(profilePkg, """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");
        return (home, profilePkg, installerPluginsDir);
    }

    /// <summary>验证 pnpm 前置「有就用」（ADR companion-pnpm-prerequisite）：PATH 探测成功即用并记版本，
    /// 不触发 npm 安装，装配继续走 plugin add。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_PnpmPreflight_UsesExistingPnpm()
    {
        (string home, string profilePkg, string installerPluginsDir) = NewPendingCompanionHome();
        var commands = new List<string>();
        var logs = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            commands.Add(IsPnpmPreflight(psi)
                ? $"{psi.FileName} {string.Join(" ", psi.ArgumentList)}"
                : "plugin-add");
            return (0, IsPnpmPreflight(psi) && psi.FileName == "pnpm" ? "10.0.0" : "installed", string.Empty);
        }

        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeOkAsync, CancellationToken.None);
            Assert.True(installed);
            Assert.Equal(["pnpm --version", "plugin-add"], commands);
            Assert.Contains(logs, l => l.Contains("pnpm 前置：PATH 已有"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 pnpm 前置「没有就装」：探测失败 → npm install -g pnpm → 复验成功 → 装配继续成功。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_PnpmPreflight_InstallsWhenMissing()
    {
        (string home, string profilePkg, string installerPluginsDir) = NewPendingCompanionHome();
        var commands = new List<string>();
        var logs = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            string command = IsPnpmPreflight(psi)
                ? $"{psi.FileName} {string.Join(" ", psi.ArgumentList)}"
                : "plugin-add";
            commands.Add(command);
            return command switch
            {
                "plugin-add" => (0, "installed", string.Empty),
                "npm install -g pnpm" => (0, string.Empty, string.Empty),
                _ => commands.Count(c => c == "pnpm --version") == 1
                    ? (1, string.Empty, "not found")   // 首次探测：未检出
                    : (0, "10.1.0", string.Empty),      // 装后复验
            };
        }

        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeOkAsync, CancellationToken.None);
            Assert.True(installed);
            Assert.Equal(["pnpm --version", "npm install -g pnpm", "pnpm --version", "plugin-add"], commands);
            Assert.Contains(logs, l => l.Contains("pnpm 前置：已装到全局"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证探测可执行缺失（Process.Start 抛 Win32Exception）按未检出收口继续装——
    /// run `40136296983` CI 实证形态：无 pnpm 环境探测即抛，修复前异常穿透到装配 catch。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_PnpmPreflight_ProbeThrowMeansMissing()
    {
        (string home, string profilePkg, string installerPluginsDir) = NewPendingCompanionHome();
        var commands = new List<string>();
        var logs = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            string command = IsPnpmPreflight(psi)
                ? $"{psi.FileName} {string.Join(" ", psi.ArgumentList)}"
                : "plugin-add";
            commands.Add(command);
            return command switch
            {
                "plugin-add" => (0, "installed", string.Empty),
                "npm install -g pnpm" => (0, string.Empty, string.Empty),
                "pnpm --version" when commands.Count(c => c == "pnpm --version") == 1
                    => throw new System.ComponentModel.Win32Exception(2, "No such file or directory"),
                _ => (0, "10.1.0", string.Empty),
            };
        }

        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeOkAsync, CancellationToken.None);
            Assert.True(installed);
            Assert.Equal(["pnpm --version", "npm install -g pnpm", "pnpm --version", "plugin-add"], commands);
            Assert.Contains(logs, l => l.Contains("pnpm 前置：已装到全局"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证系统全局 node 就位时安装命令用显式 node + npm-cli.js 形态（引导 InstallDsh 同款）——
    /// npm 壳在部分安装形态下相对解析断裂（run `40137230061`：/root/.local/bin/npm 报
    /// Cannot find module '../lib/cli.js'），裸名 npm 不可靠。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_PnpmPreflight_NpmCliExplicitForm()
    {
        (string home, string profilePkg, string installerPluginsDir) = NewPendingCompanionHome();
        string root = Path.Combine(Path.GetTempPath(), "nb-" + Guid.NewGuid().ToString("N"));
        string nodeBin = Path.Combine(root, "bin");
        string npmCli = Path.Combine(root, "lib", "node_modules", "npm", "bin", "npm-cli.js");
        Directory.CreateDirectory(nodeBin);
        Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
        File.WriteAllText(Path.Combine(nodeBin, "node"), "#!/bin/sh\n");
        File.WriteAllText(npmCli, "// cli");
        var commands = new List<string>();
        var logs = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            bool isPluginAdd = psi.ArgumentList.Contains("--profile");
            string command = isPluginAdd
                ? "plugin-add"
                : $"{psi.FileName} {string.Join(" ", psi.ArgumentList)}";
            commands.Add(command);
            return command switch
            {
                "plugin-add" => (0, "installed", string.Empty),
                "pnpm --version" when commands.Count(c => c == "pnpm --version") == 1
                    => (127, string.Empty, "start failed: No such file or directory"),
                "pnpm --version" => (0, "10.2.0", string.Empty),
                _ when command.Contains("npm-cli.js install -g pnpm") => (0, string.Empty, string.Empty),
                _ => (1, string.Empty, "unexpected"),
            };
        }

        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeOkAsync,
                CancellationToken.None, nodeBin);
            Assert.True(installed);
            Assert.Equal(4, commands.Count);
            Assert.Equal("pnpm --version", commands[0]);
            Assert.Equal($"{Path.Combine(nodeBin, "node")} {npmCli} install -g pnpm", commands[1]);
            Assert.Equal("pnpm --version", commands[2]);
            Assert.Equal("plugin-add", commands[3]);
            Assert.Contains(logs, l => l.Contains("pnpm 前置：已装到全局"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>验证 pnpm 前置安装失败时装配放弃：无 plugin add 调用、不进事务（无 staging 残留）、
    /// active 原样，日志含前置失败与放弃语义。</summary>
    [Fact]
    public async Task EnsureBundledPluginsBeforeSpawn_PnpmPreflight_AbortsWhenInstallFails()
    {
        (string home, string profilePkg, string installerPluginsDir) = NewPendingCompanionHome();
        string original = File.ReadAllText(profilePkg);
        var commands = new List<string>();
        var logs = new List<string>();
        async Task<(int Exit, string Out, string Err)> RunFakeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            string command = IsPnpmPreflight(psi)
                ? $"{psi.FileName} {string.Join(" ", psi.ArgumentList)}"
                : "plugin-add";
            commands.Add(command);
            return command switch
            {
                "pnpm --version" => (1, string.Empty, "not found"),
                "npm install -g pnpm" => (1, string.Empty, "EACCES: permission denied"),
                _ => (0, "installed", string.Empty),
            };
        }

        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                "node", "/dsh/bin.js", home, installerPluginsDir, logs.Add, RunFakeAsync, ProbeOkAsync, CancellationToken.None);
            Assert.False(installed);
            Assert.Equal(["pnpm --version", "npm install -g pnpm"], commands);
            Assert.Equal(original, File.ReadAllText(profilePkg));
            Assert.Empty(Directory.GetDirectories(home, ".tx-*"));
            Assert.Contains(logs, l => l.Contains("pnpm 前置安装失败"));
            Assert.Contains(logs, l => l.Contains("装配前置失败"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
