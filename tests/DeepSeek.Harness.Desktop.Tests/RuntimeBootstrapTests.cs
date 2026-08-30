using System.Text;
using DeepSeek.Harness.Desktop.Services;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>RuntimeBootstrap 相关测试集合必须真串行：会改写进程级 PATH 与临时目录，并行会互相污染。</summary>
[CollectionDefinition("bootstrap-node-env", DisableParallelization = true)]
public class BootstrapNodeEnvCollectionDefinition;

/// <summary>
/// RuntimeBootstrap 行为（ADR simple-shell-single-global-dsh）：系统全局 node + 全局 dsh。
/// 有系统 node → 用它 <c>npm install -g @alpha</c>（系统全局位）→ 验证 PATH `dsh --version`；没系统 node →
/// 下载最新官方 node（SHA256 校验 + 多源回落）装到系统全局前缀（需 sudo 提示手动命令）→ 再装全局 dsh。
/// npm 因权限需 sudo 时给出可手动执行的命令。hook 注入端到端状态机（对齐 OrphanDshReaper 委托注入风格）。
/// </summary>
[Collection("bootstrap-node-env")]
public class RuntimeBootstrapTests
{
    /// <summary>验证当前平台下 Node 归档文件名含 node-v{version}- 前缀，扩展名落在 .tar.xz/.tar.gz/zip 之一。</summary>
    [Fact]
    public void NodeArchiveFileName_CurrentPlatform_HasVersionAndExtension()
    {
        string? name = RuntimeBootstrap.NodeArchiveFileName("24.20.0");
        Assert.NotNull(name);
        Assert.Contains("node-v24.20.0-", name);
        Assert.Matches(@"\.(tar\.(xz|gz)|zip)$", name);
    }

    /// <summary>验证 npm-cli.js 在 Node 发行包内的相对路径以 npm/bin/npm-cli.js 结尾。</summary>
    [Fact]
    public void NpmCliRelativePath_EndsWithNpmCli()
    {
        Assert.EndsWith(Path.Combine("npm", "bin", "npm-cli.js"), RuntimeBootstrap.NpmCliRelativePath());
    }

    /// <summary>验证 Win32 扩展前缀（\\?\ 与 \\?\UNC\）被剥离、普通路径保留。</summary>
    [Fact]
    public void StripExtendedPrefix_RemovesWin32Prefixes_KeepsPlainPaths()
    {
        Assert.Equal(@"C:\app\node.exe", RuntimeBootstrap.StripExtendedPrefix(@"\\?\C:\app\node.exe"));
        Assert.Equal(@"\\server\share\node.exe", RuntimeBootstrap.StripExtendedPrefix(@"\\?\UNC\server\share\node.exe"));
        Assert.Equal("/usr/bin/node", RuntimeBootstrap.StripExtendedPrefix("/usr/bin/node"));
        Assert.Equal(@"C:\plain", RuntimeBootstrap.StripExtendedPrefix(@"C:\plain"));
    }

    /// <summary>验证从 nodejs.org index.json 解析最新版（剥前导 v、取数组首个条目）；空数组返回 null。</summary>
    [Fact]
    public void ParseLatestNodeVersion_TakesFirst_StripsV()
    {
        Assert.Equal("24.20.0", RuntimeBootstrap.ParseLatestNodeVersion("""[{"version":"v24.20.0","lts":"Jod"},{"version":"v23.8.0"}]"""));
        Assert.Null(RuntimeBootstrap.ParseLatestNodeVersion("[]"));
    }

    /// <summary>验证按文件名（大小写不敏感）从 SHASUMS 文本选出对应摘要。</summary>
    [Fact]
    public void SelectSha256_MatchesFileNameCaseInsensitiveHash()
    {
        const string shasums = "abc123def  node-v24.20.0-linux-x64.tar.xz\nffff0000  node-v24.20.0-win-x64.zip\n";
        Assert.Equal("abc123def", RuntimeBootstrap.SelectSha256(shasums, "node-v24.20.0-linux-x64.tar.xz"));
        Assert.Null(RuntimeBootstrap.SelectSha256(shasums, "node-v24.20.0-darwin-arm64.tar.gz"));
    }

    /// <summary>验证 node 全局 bin 目录解析（unix prefix/bin、Windows prefix 根）。</summary>
    [Fact]
    public void NodeBinDir_PlatformLayout()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"C:\node", RuntimeBootstrap.NodeBinDir(@"C:\node"));
        }
        else
        {
            Assert.Equal("/node/bin", RuntimeBootstrap.NodeBinDir("/node"));
        }
    }

    /// <summary>验证系统全局 node 默认安装前缀：Windows ProgramFiles\nodejs、Unix 用户主目录 .local。</summary>
    [Fact]
    public void DefaultGlobalNodePrefix_IsProgramFilesNodejs_OrUserLocal()
    {
        string prefix = RuntimeBootstrap.DefaultGlobalNodePrefix();
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith("nodejs", prefix);
        }
        else
        {
            Assert.Equal(".local", Path.GetFileName(prefix));
        }
    }

    /// <summary>验证递归复制把源树拷进目标（覆盖/合并）。</summary>
    [Fact]
    public void CopyDirectoryRecursive_CopiesTree_Overwrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "copy-" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(root, "src");
        string dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(Path.Combine(src, "bin"));
        File.WriteAllText(Path.Combine(src, "bin", "node"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(src, "lib.js"), "x");
        try
        {
            RuntimeBootstrap.CopyDirectoryRecursive(src, dst);
            Assert.True(File.Exists(Path.Combine(dst, "bin", "node")));
            Assert.Equal("#!/bin/sh\n", File.ReadAllText(Path.Combine(dst, "bin", "node")));
            Assert.True(File.Exists(Path.Combine(dst, "lib.js")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>构造"全局 node 可用"的 fake hooks：复用 PATH node/npm，npm install -g 与 dsh --version 分别可配。</summary>
    private static (RuntimeBootstrapHooks Hooks, List<string> Calls) GlobalNodeHooks(
        (int Exit, string Err)? installResult = null, string? version = "0.1.2-alpha.3")
    {
        var calls = new List<string>();
        var hooks = new RuntimeBootstrapHooks(
            DownloadFileAsync: (url, dest, ct) => Task.CompletedTask,
            FetchTextAsync: (url, ct) => Task.FromResult(string.Empty),
            ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
            RunProcessAsync: (exe, args, ct) =>
            {
                string joined = string.Join(' ', args);
                calls.Add($"run:{exe}:{joined}");
                if (joined.Contains("install", StringComparison.Ordinal))
                {
                    return Task.FromResult(installResult is { } r
                        ? (r.Exit, string.Empty, r.Err)
                        : (0, string.Empty, string.Empty));
                }

                return Task.FromResult(version is null
                    ? (1, string.Empty, "no version")
                    : (0, version + Environment.NewLine, string.Empty));
            },
            ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((Path.Combine("/fake", "node"), Path.Combine("/fake", "npm-cli.js"))));
        return (hooks, calls);
    }

    /// <summary>构造"无系统 node、下载装到系统全局"的 fake hooks（index.json 取最新 + 官方摘要 + 解压 + npm install -g）。</summary>
    private static RuntimeBootstrapHooks NoNodeInstallHooks(string prefix, string nodeVersion = "24.20.0")
    {
        const string archiveBytes = "archive-bytes";
        string fileName = RuntimeBootstrap.NodeArchiveFileName(nodeVersion)!;
        string sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(archiveBytes))).ToLowerInvariant();
        return new RuntimeBootstrapHooks(
            DownloadFileAsync: (url, dest, ct) =>
            {
                File.WriteAllText(dest, archiveBytes);
                return Task.CompletedTask;
            },
            FetchTextAsync: (url, ct) =>
            {
                if (url.EndsWith("/index.json", StringComparison.Ordinal))
                {
                    return Task.FromResult($$""" [{"version":"v{{nodeVersion}}","lts":"Jod"}] """);
                }

                return Task.FromResult($"{sha}  {fileName}\n");
            },
            ExtractArchiveAsync: (archive, destDir, ct) =>
            {
                string inner = Path.Combine(destDir, $"node-v{nodeVersion}-fake");
                Directory.CreateDirectory(Path.Combine(inner, "bin"));
                string node = OperatingSystem.IsWindows() ? Path.Combine(inner, "node.exe") : Path.Combine(inner, "bin", "node");
                File.WriteAllText(node, "#!/bin/sh\n");
                string npmCli = Path.Combine(inner, OperatingSystem.IsWindows() ? "node_modules" : "lib", "node_modules", "npm", "bin", "npm-cli.js");
                Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
                File.WriteAllText(npmCli, "// npm\n");
                return Task.CompletedTask;
            },
            RunProcessAsync: (exe, args, ct) =>
            {
                if (string.Join(' ', args).Contains("install", StringComparison.Ordinal))
                {
                    return Task.FromResult((0, string.Empty, string.Empty));
                }

                return Task.FromResult((0, "0.1.2-alpha.3" + Environment.NewLine, string.Empty));
            },
            ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));
    }

    /// <summary>验证全局 node 复用端到端成功：npm install -g（无 --prefix，系统全局）→ 验证 PATH dsh。</summary>
    [Fact]
    public async Task RunAsync_GlobalNodeReuse_InstallsGlobal_Succeeds()
    {
        (RuntimeBootstrapHooks hooks, List<string> calls) = GlobalNodeHooks();
        var progress = new List<BootstrapProgress>();
        BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(
            new RuntimeBootstrapOptions { DshSpec = "@deepseek-ai/dsh@alpha" },
            progress.Add,
            hooks,
            CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("0.1.2-alpha.3", outcome.DshVersion);
        Assert.Contains(progress, p => p.Step == BootstrapStep.Ready && !p.Failed);
        Assert.Contains(calls, c => c.Contains("install -g", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("--prefix", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.StartsWith("run:tar", StringComparison.Ordinal));
    }

    /// <summary>验证 npm install -g 参数含全局标志与 @alpha spec（装/更新到 alpha 预发布通道）。</summary>
    [Fact]
    public async Task RunAsync_InstallArgs_ContainGlobalAndAlphaSpec()
    {
        (RuntimeBootstrapHooks hooks, List<string> calls) = GlobalNodeHooks();
        BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(
            new RuntimeBootstrapOptions { DshSpec = "@deepseek-ai/dsh@alpha" },
            _ => { },
            hooks,
            CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        string installCall = calls.Single(c => c.Contains("install", StringComparison.Ordinal));
        Assert.Contains("install -g", installCall);
        Assert.Contains("@deepseek-ai/dsh@alpha", installCall);
        Assert.Contains("npm-cli.js", installCall);
    }

    /// <summary>验证 npm install 返回非零时透出 stderr（E404）且停在 InstallDsh 步。</summary>
    [Fact]
    public async Task RunAsync_NpmInstallFails_FailsLoudWithStderr()
    {
        (RuntimeBootstrapHooks hooks, _) = GlobalNodeHooks(installResult: (1, "E404: not found"));
        BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(new RuntimeBootstrapOptions(), _ => { }, hooks, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal(BootstrapStep.InstallDsh, outcome.Step);
        Assert.Contains("E404", outcome.Error);
    }

    /// <summary>验证 npm install 因权限（EACCES）失败时提示用户手动执行 sudo 安装命令，而非静默失败。</summary>
    [Fact]
    public async Task RunAsync_PermissionError_SuggestsSudoCommand()
    {
        (RuntimeBootstrapHooks hooks, _) = GlobalNodeHooks(installResult: (1, "npm error code EACCES: permission denied"));
        BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(
            new RuntimeBootstrapOptions { DshSpec = "@deepseek-ai/dsh@alpha" }, _ => { }, hooks, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal(BootstrapStep.InstallDsh, outcome.Step);
        Assert.Contains("sudo npm install -g @deepseek-ai/dsh@alpha", outcome.Error);
    }

    /// <summary>验证安装成功但 PATH dsh --version 无法解析时失败，停在 VerifyDsh 步。</summary>
    [Fact]
    public async Task RunAsync_VerifyFails_ReturnsError()
    {
        (RuntimeBootstrapHooks hooks, _) = GlobalNodeHooks(version: null);
        BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(new RuntimeBootstrapOptions(), _ => { }, hooks, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal(BootstrapStep.VerifyDsh, outcome.Step);
        Assert.Null(outcome.DshVersion);
    }

    /// <summary>验证无系统 node 时下载最新官方 node 装到全局前缀、暴露该 node 的 bin 到 PATH、再装全局 dsh 成功。</summary>
    [Fact]
    public async Task RunAsync_NoGlobalNode_DownloadsAndInstallsGlobal_Succeeds()
    {
        string root = Path.Combine(Path.GetTempPath(), "boot-" + Guid.NewGuid().ToString("N"));
        string prefix = Path.Combine(root, "sysprefix");
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            RuntimeBootstrapHooks hooks = NoNodeInstallHooks(prefix);
            var progress = new List<BootstrapProgress>();
            BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { NodeGlobalPrefix = prefix },
                progress.Add,
                hooks,
                CancellationToken.None);

            Assert.True(outcome.Success, outcome.Error);
            Assert.Equal("0.1.2-alpha.3", outcome.DshVersion);
            string nodePath = Path.Combine(prefix, OperatingSystem.IsWindows() ? "node.exe" : Path.Combine("bin", "node"));
            Assert.True(File.Exists(nodePath), "node 应装到系统全局前缀");
            Assert.True(File.Exists(Path.Combine(prefix, RuntimeBootstrap.NpmCliRelativePath())), "npm-cli 应装到系统全局前缀");
            string nodeBinDir = RuntimeBootstrap.NodeBinDir(prefix);
            Assert.Contains(nodeBinDir, Environment.GetEnvironmentVariable("PATH") ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>验证系统全局前缀已有 node（此前安装/用户手动）时复用，不再下载。</summary>
    [Fact]
    public async Task RunAsync_ReusesGlobalPrefixNode_SkipsDownload()
    {
        string root = Path.Combine(Path.GetTempPath(), "boot-" + Guid.NewGuid().ToString("N"));
        string prefix = Path.Combine(root, "sysprefix");
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string nodePath = Path.Combine(prefix, OperatingSystem.IsWindows() ? "node.exe" : Path.Combine("bin", "node"));
            Directory.CreateDirectory(Path.GetDirectoryName(nodePath)!);
            File.WriteAllText(nodePath, "#!/bin/sh\n");
            string npmCli = Path.Combine(prefix, RuntimeBootstrap.NpmCliRelativePath());
            Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
            File.WriteAllText(npmCli, "// npm\n");

            bool downloaded = false;
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, dest, ct) => { downloaded = true; return Task.CompletedTask; },
                FetchTextAsync: (url, ct) => Task.FromResult(string.Empty),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) => Task.FromResult((0, "0.1.2-alpha.3" + Environment.NewLine, string.Empty)),
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

            BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { NodeGlobalPrefix = prefix },
                _ => { },
                hooks,
                CancellationToken.None);

            Assert.True(outcome.Success, outcome.Error);
            Assert.False(downloaded, "系统全局前缀已有 node 时不得再下载");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>验证写系统全局位权限不足时，引导错误态提示用户以管理员安装 Node.js（不静默失败、不做私有落位）。</summary>
    [Fact]
    public async Task RunAsync_NodeInstallPermissionDenied_PromptsAdmin()
    {
        string root = Path.Combine(Path.GetTempPath(), "boot-" + Guid.NewGuid().ToString("N"));
        string blockedPrefix = Path.Combine(root, "sysprefix-as-file");
        Directory.CreateDirectory(root);
        File.WriteAllText(blockedPrefix, "i am a file, not a dir\n");
        try
        {
            RuntimeBootstrapHooks hooks = NoNodeInstallHooks(blockedPrefix);
            BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { NodeGlobalPrefix = blockedPrefix },
                _ => { },
                hooks,
                CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Equal(BootstrapStep.EnsureNode, outcome.Step);
            Assert.Contains("权限不足", outcome.Error);
            Assert.Contains("Node.js", outcome.Error);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>验证挂起的下载触发步超时（StepTimeoutMinutes=0）转含「超时」的可读失败态，停在 EnsureNode 步。</summary>
    [Fact]
    public async Task RunAsync_StepTimeout_FailsAsRetryableError()
    {
        // 钉临时全局前缀：默认 ~/.local 在真机上可能已有 node（用户手动/此前安装），会让
        // 「复用已装到系统全局的 Node」分支短路、下载永不触发——隔离机器状态，强制走下载路径命中步超时。
        string prefix = Path.Combine(Path.GetTempPath(), "dsh-timeout-" + Guid.NewGuid().ToString("N"));
        string fileName = RuntimeBootstrap.NodeArchiveFileName("24.20.0")!;
        string sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("archive-bytes"))).ToLowerInvariant();
        var hooks = new RuntimeBootstrapHooks(
            DownloadFileAsync: async (url, dest, ct) => await Task.Delay(Timeout.Infinite, ct),
            FetchTextAsync: (url, ct) =>
            {
                // 让下载步骤（而非摘要取文本）成为挂起点：先返回合法 index.json 与 SHASUMS
                if (url.EndsWith("/index.json", StringComparison.Ordinal))
                {
                    return Task.FromResult("""[{"version":"v24.20.0","lts":"Jod"}]""");
                }

                return Task.FromResult($"{sha}  {fileName}\n");
            },
            ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
            RunProcessAsync: (exe, args, ct) => Task.FromResult((0, string.Empty, string.Empty)),
            ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

        BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(
            new RuntimeBootstrapOptions { StepTimeoutMinutes = 0, NodeGlobalPrefix = prefix },
            _ => { },
            hooks,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("超时", outcome.Error);
        Assert.Equal(BootstrapStep.EnsureNode, outcome.Step);
    }
}

/// <summary>子进程文本流 UTF-8 单点契约。</summary>
public class Utf8TextStreamsTests
{
    /// <summary>验证 ProcessStartInfo 的标准输出与标准错误流编码同时被设为 UTF-8。</summary>
    [Fact]
    public void UseUtf8TextStreams_SetsBothEncodings()
    {
        var psi = new System.Diagnostics.ProcessStartInfo { RedirectStandardOutput = true, RedirectStandardError = true };
        HarnessRuntimeHost.UseUtf8TextStreams(psi);
        Assert.Equal(Encoding.UTF8, psi.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, psi.StandardErrorEncoding);
    }
}

/// <summary>RuntimeBootstrapGate 记序语义 + BootstrapCommandRouter 路由契约。</summary>
public class BootstrapGateAndRouterTests
{
    /// <summary>验证 RuntimeBootstrapGate 记序往返：Signal 后 IsSignaled=true，Reset 后回到 false。</summary>
    [Fact]
    public void Gate_SignalAndReset_RoundTrip()
    {
        var gate = new RuntimeBootstrapGate();
        Assert.False(gate.IsSignaled);
        gate.Signal();
        Assert.True(gate.IsSignaled);
        gate.Reset();
        Assert.False(gate.IsSignaled);
    }

    /// <summary>验证 desktop.bootstrap.retry 可路由且同步触发闸门信号，recovery.exit 不可路由。</summary>
    [Fact]
    public void Router_RetryCommand_SignalsGate()
    {
        var gate = new RuntimeBootstrapGate();
        var router = new BootstrapCommandRouter(gate);
        Assert.True(router.CanRoute("desktop.bootstrap.retry"));
        Assert.False(router.CanRoute("desktop.recovery.exit"));
        ValueTask<string> result = router.RouteAsync("desktop.bootstrap.retry", default, null!, CancellationToken.None);
        Assert.True(result.IsCompletedSuccessfully);
        Assert.True(gate.IsSignaled);
    }

    /// <summary>验证路由未注册命令 desktop.unknown 抛 RynCommandNotFoundException。</summary>
    [Fact]
    public async Task Router_UnknownCommand_ThrowsRynCommandNotFound()
    {
        var router = new BootstrapCommandRouter(new RuntimeBootstrapGate());
        await Assert.ThrowsAsync<RynCommandNotFoundException>(
            () => router.RouteAsync("desktop.unknown", default, null!, CancellationToken.None).AsTask());
    }
}

/// <summary>RuntimeBootstrapOptions 配置装载（ADR simple-shell-single-global-dsh）。</summary>
public class RuntimeBootstrapOptionsTests
{
    /// <summary>验证 appsettings 缺 RuntimeBootstrap 节时返回默认值（dsh @alpha、步超时 10m、官方 dist）。</summary>
    [Fact]
    public void Load_MissingSection_ReturnsDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"Update":{}}""");
            var options = RuntimeBootstrapOptions.Load(dir);
            Assert.Equal("@deepseek-ai/dsh@alpha", options.DshSpec);
            Assert.Equal(10, options.StepTimeoutMinutes);
            Assert.Equal("https://nodejs.org/dist", options.NodeDistBaseUrl);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证合法配置节逐项覆盖默认值：dsh spec/步超时/分发与镜像/全局前缀。</summary>
    [Fact]
    public void Load_ValidSection_Overrides()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
                {"RuntimeBootstrap":{"DshSpec":"@deepseek-ai/dsh@0.1.1-rc.2","StepTimeoutMinutes":5,"NodeDistBaseUrl":"https://mirror.example/dist","NodeMirrorBaseUrl":"https://cdn.example.com/node","NodeGlobalPrefix":"/usr/local"}}
                """);
            var options = RuntimeBootstrapOptions.Load(dir);
            Assert.Equal("@deepseek-ai/dsh@0.1.1-rc.2", options.DshSpec);
            Assert.Equal(5, options.StepTimeoutMinutes);
            Assert.Equal("https://mirror.example/dist", options.NodeDistBaseUrl);
            Assert.Equal("https://cdn.example.com/node", options.NodeMirrorBaseUrl);
            Assert.Equal("/usr/local", options.NodeGlobalPrefix);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证坏 JSON 时 fail-safe 回退默认值而非抛异常，配置损坏不阻塞引导。</summary>
    [Fact]
    public void Load_BrokenJson_FailsSafeToDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{not json");
            var options = RuntimeBootstrapOptions.Load(dir);
            Assert.Equal("@deepseek-ai/dsh@alpha", options.DshSpec);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
