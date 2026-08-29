using System.Net;
using System.Net.Http;
using System.Text;
using DeepSeek.Harness.Desktop.Services;
using Ryn.Ipc;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// RuntimeBootstrap 行为：纯函数坐标/校验 + hooks 注入的端到端状态机（下载/安装/验证全 fake，
/// 对齐 OrphanDshReaper 委托注入风格）。download 路径的摘要契约 = 摘要先行（ADR reference-alignment 批次三）。
/// </summary>
public class RuntimeBootstrapTests
{
    // —— 纯函数 ——

    [Fact]
    public void NodeArchiveFileName_CurrentPlatform_HasVersionAndExtension()
    {
        var name = RuntimeBootstrap.NodeArchiveFileName("24.20.0");
        Assert.NotNull(name);
        Assert.Contains("node-v24.20.0-", name);
        Assert.Matches(@"\.(tar\.(xz|gz)|zip)$", name);
    }

    [Fact]
    public void NpmCliRelativePath_EndsWithNpmCli()
    {
        Assert.EndsWith(Path.Combine("npm", "bin", "npm-cli.js"), RuntimeBootstrap.NpmCliRelativePath());
    }

    [Fact]
    public void SelectSha256_MatchesFileNameCaseInsensitiveHash()
    {
        const string shasums = "abc123def  node-v24.20.0-linux-x64.tar.xz\n" +
                               "ffff0000  node-v24.20.0-win-x64.zip\n";
        Assert.Equal("abc123def", RuntimeBootstrap.SelectSha256(shasums, "node-v24.20.0-linux-x64.tar.xz"));
        Assert.Equal("ffff0000", RuntimeBootstrap.SelectSha256(shasums, "node-v24.20.0-win-x64.zip"));
        Assert.Null(RuntimeBootstrap.SelectSha256(shasums, "node-v24.20.0-darwin-arm64.tar.gz"));
    }

    [Fact]
    public void StripExtendedPrefix_RemovesWin32Prefixes_KeepsPlainPaths()
    {
        Assert.Equal(@"C:\app\node.exe", RuntimeBootstrap.StripExtendedPrefix(@"\\?\C:\app\node.exe"));
        Assert.Equal(@"\\server\share\node.exe", RuntimeBootstrap.StripExtendedPrefix(@"\\?\UNC\server\share\node.exe"));
        Assert.Equal("/usr/bin/node", RuntimeBootstrap.StripExtendedPrefix("/usr/bin/node"));
        Assert.Equal(@"C:\plain", RuntimeBootstrap.StripExtendedPrefix(@"C:\plain"));
    }

    // —— 下载/安装端到端（hooks 注入） ——

    /// <summary>构造一个临时根目录 + 其下 runtime 布局（下载/解压/备份兄弟目录同根，便于整体清理）。</summary>
    private static (string Root, string RuntimeDir) NewLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (root, Path.Combine(root, "runtime"));
    }

    /// <summary>构造一个假「解压完成」的 Node 发行目录（node + npm-cli + dsh 入口）。</summary>
    private static void FakeExtractNode(string runtimeDir)
    {
        Directory.CreateDirectory(runtimeDir);
        var node = Path.Combine(runtimeDir, OperatingSystem.IsWindows() ? "node.exe" : "node");
        File.WriteAllText(node, "#!/bin/sh\n");
        var npmCli = Path.Combine(runtimeDir, RuntimeBootstrap.NpmCliRelativePath());
        Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
        File.WriteAllText(npmCli, "// npm\n");
        var bin = Path.Combine(runtimeDir, "node_modules", "@deepseek-ai", "dsh", "lib");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "bin.js"), "// dsh\n");
    }

    /// <summary>构造 happy-path hooks：摘要先行契约——FetchTextAsync 用常量摘要、DownloadFileAsync 写常量字节使其自洽。</summary>
    private static (RuntimeBootstrapHooks Hooks, List<string> Log) HappyHooks(string runtimeDir)
    {
        const string archiveBytes = "archive-bytes";
        var calls = new List<string>();
        var fileName = RuntimeBootstrap.NodeArchiveFileName("24.20.0")!;
        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(archiveBytes))).ToLowerInvariant();
        var hooks = new RuntimeBootstrapHooks(
            DownloadFileAsync: (url, dest, ct) =>
            {
                calls.Add($"download:{url}");
                File.WriteAllText(dest, archiveBytes);
                return Task.CompletedTask;
            },
            FetchTextAsync: (url, ct) =>
            {
                calls.Add($"shasums:{url}");
                return Task.FromResult($"{sha}  {fileName}\n");
            },
            ExtractArchiveAsync: (archive, destDir, ct) =>
            {
                calls.Add($"extract:{archive}");
                // 模拟 tar 解压出真实发行包布局：内层目录含 bin/node 与 npm 模块树，外加
                // include/CHANGELOG 等「非运行时」残余——归一化（生产代码）只搬 node+模块树、
                // 清残余，保证 runtimeDir 与闭包同构（B1）
                var inner = Path.Combine(destDir, "node-v24.20.0-fake");
                Directory.CreateDirectory(Path.Combine(inner, "bin"));
                File.WriteAllText(Path.Combine(inner, "bin", "node"), "#!/bin/sh\n");
                Directory.CreateDirectory(Path.Combine(inner, "include"));
                File.WriteAllText(Path.Combine(inner, "include", "x.h"), "// leftover\n");
                var npmTree = OperatingSystem.IsWindows() ? "node_modules" : "lib";
                var npmCli = Path.Combine(inner, npmTree, "node_modules", "npm", "bin", "npm-cli.js");
                Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
                File.WriteAllText(npmCli, "// npm\n");
                return Task.CompletedTask;
            },
            RunProcessAsync: (exe, args, ct) =>
            {
                var joined = string.Join(' ', args);
                calls.Add($"run:{exe}:{joined}");
                if (joined.Contains("install", StringComparison.Ordinal))
                {
                    // npm install fake：装出 dsh 包（extract 已预置，幂等重建）
                    FakeExtractNode(runtimeDir);
                    return Task.FromResult((0, string.Empty, string.Empty));
                }

                // node bin.js --version
                return Task.FromResult((0, "v0.1.1-rc.2" + Environment.NewLine, string.Empty));
            },
            ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));
        return (hooks, calls);
    }

    [Fact]
    public async Task RunAsync_DownloadPath_Succeeds_AndVerifiesArtifacts()
    {
        var (root, runtimeDir) = NewLayout();
        var (hooks, calls) = HappyHooks(runtimeDir);
        try
        {
            var progress = new List<BootstrapProgress>();
            var outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { NodeVersion = "24.20.0", DshSpec = "@deepseek-ai/dsh@latest" },
                runtimeDir,
                progress.Add,
                hooks,
                CancellationToken.None);

            Assert.True(outcome.Success, outcome.Error);
            Assert.NotNull(outcome.Runtime);
            Assert.True(File.Exists(outcome.Runtime!.Value.NodeExe));
            Assert.True(File.Exists(outcome.Runtime.Value.DshEntry));
            // npm 静默 no-op 防线：安装前必须落最小 package.json（沙箱实证缺它 exit 0 不装）
            Assert.True(File.Exists(Path.Combine(runtimeDir, "package.json")));
            // B1：发行包「非运行时」残余（include/CHANGELOG 等）不得被 swap 进 runtimeDir
            Assert.False(Directory.Exists(Path.Combine(runtimeDir, "extract")));
            Assert.Contains(progress, p => p.Step == BootstrapStep.Ready && !p.Failed);
            Assert.Contains(calls, c => c.StartsWith("download:", StringComparison.Ordinal));
            Assert.Contains(calls, c => c.StartsWith("shasums:", StringComparison.Ordinal));
            Assert.Contains(calls, c => c.Contains("install", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ShaMismatch_FailsLoud()
    {
        var (root, runtimeDir) = NewLayout();
        try
        {
            var fileName = RuntimeBootstrap.NodeArchiveFileName("24.20.0")!;
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, dest, ct) =>
                {
                    File.WriteAllText(dest, "archive-bytes");
                    return Task.CompletedTask;
                },
                FetchTextAsync: (url, ct) => Task.FromResult($"deadbeef  {fileName}\n"),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) => Task.FromResult((0, string.Empty, string.Empty)),
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));
            var progress = new List<BootstrapProgress>();
            var outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { NodeVersion = "24.20.0" },
                runtimeDir,
                progress.Add,
                hooks,
                CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Contains("SHA256", outcome.Error);
            // 供应链校验失败绝不产出运行时
            Assert.Null(outcome.Runtime);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_LocalNodeReuse_SkipsDownload()
    {
        var runtimeDir = Path.Combine(Path.GetTempPath(), "boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDir);
        try
        {
            // 本机 node 布局：/tmp/xx/node + /tmp/xx/lib/node_modules/npm/bin/npm-cli.js（linux/mac）
            var fakeNodeDir = Path.Combine(Path.GetTempPath(), "bootnode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fakeNodeDir);
            var nodePath = Path.Combine(fakeNodeDir, "node");
            File.WriteAllText(nodePath, "#!/bin/sh\n");
            var npmCli = Path.Combine(fakeNodeDir, RuntimeBootstrap.NpmCliRelativePath());
            Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
            File.WriteAllText(npmCli, "// npm\n");

            var downloaded = false;
            var calls = new List<string>();
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, dest, ct) =>
                {
                    downloaded = true;
                    return Task.CompletedTask;
                },
                FetchTextAsync: (url, ct) => Task.FromResult(string.Empty),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) =>
                {
                    var joined = string.Join(' ', args);
                    calls.Add(joined);
                    if (joined.Contains("install", StringComparison.Ordinal))
                    {
                        FakeExtractNode(runtimeDir);
                    }

                    return Task.FromResult((0, "v24.20.0" + Environment.NewLine, string.Empty));
                },
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((nodePath, "24")));

            var outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { MinimumLocalNodeMajor = 22 },
                runtimeDir,
                _ => { },
                hooks,
                CancellationToken.None);

            Assert.True(outcome.Success, outcome.Error);
            Assert.False(downloaded, "本机 node 可复用时不得触发下载");
            Assert.Contains(calls, c => c.Contains("install", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(runtimeDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_StepTimeout_FailsAsRetryableError()
    {
        // 步超时（R2 评审 B3）：挂起的下载不再无限 spinner，转人可读失败态走重试页。
        // StepTimeoutMinutes=0 → CancelAfter 立即触发，测试无需等待。
        // 摘要先行：FetchTextAsync 先返回有效摘要，下载（挂起）才因步超时触发。
        var (root, runtimeDir) = NewLayout();
        try
        {
            var fileName = RuntimeBootstrap.NodeArchiveFileName("24.20.0")!;
            var sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("archive-bytes"))).ToLowerInvariant();
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: async (url, dest, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                },
                FetchTextAsync: (url, ct) => Task.FromResult($"{sha}  {fileName}\n"),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) => Task.FromResult((0, string.Empty, string.Empty)),
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

            var outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { StepTimeoutMinutes = 0 },
                runtimeDir,
                _ => { },
                hooks,
                CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Contains("超时", outcome.Error);
            Assert.Equal(BootstrapStep.EnsureNode, outcome.Step);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NpmInstallFails_FailsLoudWithStderr()
    {
        var runtimeDir = Path.Combine(Path.GetTempPath(), "boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDir);
        try
        {
            // 已有 node（跳过下载），npm install 返回失败
            File.WriteAllText(Path.Combine(runtimeDir, OperatingSystem.IsWindows() ? "node.exe" : "node"), "node");
            var npmCli = Path.Combine(runtimeDir, RuntimeBootstrap.NpmCliRelativePath());
            Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
            File.WriteAllText(npmCli, "// npm\n");

            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, dest, ct) => Task.CompletedTask,
                FetchTextAsync: (url, ct) => Task.FromResult(string.Empty),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) => Task.FromResult((1, string.Empty, "E404: not found")),
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

            var outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions(),
                runtimeDir,
                _ => { },
                hooks,
                CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Contains("E404", outcome.Error);
        }
        finally
        {
            Directory.Delete(runtimeDir, recursive: true);
        }
    }

    // —— batch 3 · 多源回落 ——

    [Fact]
    public async Task RunAsync_OfficialDownloadFails_FallsBackToMirror()
    {
        var (root, runtimeDir) = NewLayout();
        try
        {
            var fileName = RuntimeBootstrap.NodeArchiveFileName("24.20.0")!;
            var calls = new List<string>();
            var sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("archive-bytes"))).ToLowerInvariant();
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, dest, ct) =>
                {
                    calls.Add(url);
                    // 官方（nodejs.org）失败，镜像（npmmirror）成功；dest 现有字节保留供续传
                    if (url.Contains("nodejs.org", StringComparison.Ordinal))
                    {
                        throw new HttpRequestException("official unreachable");
                    }

                    File.WriteAllText(dest, "archive-bytes");
                    return Task.CompletedTask;
                },
                FetchTextAsync: (url, ct) => Task.FromResult($"{sha}  {fileName}\n"),
                ExtractArchiveAsync: (archive, destDir, ct) =>
                {
                    var inner = Path.Combine(destDir, "node-v24.20.0-fake");
                    Directory.CreateDirectory(Path.Combine(inner, "bin"));
                    File.WriteAllText(Path.Combine(inner, "bin", "node"), "#!/bin/sh\n");
                    var npmTree = OperatingSystem.IsWindows() ? "node_modules" : "lib";
                    var npmCli = Path.Combine(inner, npmTree, "node_modules", "npm", "bin", "npm-cli.js");
                    Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
                    File.WriteAllText(npmCli, "// npm\n");
                    return Task.CompletedTask;
                },
                RunProcessAsync: (exe, args, ct) =>
                {
                    if (string.Join(' ', args).Contains("install", StringComparison.Ordinal))
                    {
                        FakeExtractNode(runtimeDir);
                    }

                    return Task.FromResult((0, "v0.1.1-rc.2" + Environment.NewLine, string.Empty));
                },
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

            var outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { NodeVersion = "24.20.0" },
                runtimeDir,
                _ => { },
                hooks,
                CancellationToken.None);

            Assert.True(outcome.Success, outcome.Error);
            // 官方失败后确实切到镜像（npmmirror）
            Assert.Contains(calls, c => c.Contains("cdn.npmmirror.com", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MirrorDisabled_PrimaryOnly_SingleSourceFailureFails()
    {
        var (root, runtimeDir) = NewLayout();
        try
        {
            var fileName = RuntimeBootstrap.NodeArchiveFileName("24.20.0")!;
            var sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("archive-bytes"))).ToLowerInvariant();
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, dest, ct) =>
                    throw new HttpRequestException("single source unreachable"),
                FetchTextAsync: (url, ct) => Task.FromResult($"{sha}  {fileName}\n"),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) => Task.FromResult((0, string.Empty, string.Empty)),
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

            var outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { NodeMirrorBaseUrl = string.Empty },
                runtimeDir,
                _ => { },
                hooks,
                CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Contains("下载", outcome.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_OfficialDigestUnreachable_FailsLoud_NeverDownloads()
    {
        var (root, runtimeDir) = NewLayout();
        try
        {
            var downloaded = false;
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, dest, ct) =>
                {
                    downloaded = true;
                    return Task.CompletedTask;
                },
                FetchTextAsync: (url, ct) => throw new HttpRequestException("official shasums unreachable"),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) => Task.FromResult((0, string.Empty, string.Empty)),
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

            var outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions(),
                runtimeDir,
                _ => { },
                hooks,
                CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Contains("摘要", outcome.Error);
            // 无可信摘要 → 绝不用镜像/绝不下载归档（防投毒）
            Assert.False(downloaded, "官方摘要不可达时不得下载归档");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadWithFallback_PrimaryThrows_UsesNextSource()
    {
        var dest = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        var urls = new List<string>();
        var hooks = new RuntimeBootstrapHooks(
            DownloadFileAsync: (url, destPath, ct) =>
            {
                urls.Add(url);
                if (url.Contains("primary", StringComparison.Ordinal))
                {
                    throw new HttpRequestException("primary fail");
                }

                File.WriteAllText(destPath, "ok");
                return Task.CompletedTask;
            },
            FetchTextAsync: (url, ct) => Task.FromResult(string.Empty),
            ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
            RunProcessAsync: (exe, args, ct) => Task.FromResult((0, string.Empty, string.Empty)),
            ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

        await RuntimeBootstrap.DownloadWithFallbackAsync(
            new[] { "https://primary", "https://mirror" }, dest, hooks, CancellationToken.None);

        Assert.Equal(new[] { "https://primary", "https://mirror" }, urls);
        Assert.Equal("ok", File.ReadAllText(dest));
        File.Delete(dest);
    }

    [Fact]
    public async Task DownloadWithFallback_AllFail_Throws()
    {
        var dest = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, destPath, ct) => throw new HttpRequestException("fail"),
                FetchTextAsync: (url, ct) => Task.FromResult(string.Empty),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) => Task.FromResult((0, string.Empty, string.Empty)),
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => RuntimeBootstrap.DownloadWithFallbackAsync(
                    new[] { "https://a", "https://b" }, dest, hooks, CancellationToken.None));
            Assert.Contains("所有候选源均失败", ex.Message);
        }
        finally
        {
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }
        }
    }

    // —— batch 3 · 断点续传（Range） ——

    [Fact]
    public async Task DownloadResumable_ExistingAnd206_Appends()
    {
        var dest = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(dest, "AA");
        var handler = new FakeHttpHandler((HttpStatusCode.PartialContent, Encoding.UTF8.GetBytes("BB")));
        var http = new HttpClient(handler);
        try
        {
            await RuntimeBootstrap.DownloadResumableAsync(http, "https://src/file", dest, CancellationToken.None);
            Assert.Equal("AABB", File.ReadAllText(dest));
            Assert.Equal(new[] { "bytes=2-" }, handler.Ranges);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadResumable_ExistingAnd200_RestartsFromZero()
    {
        var dest = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(dest, "AAAA");
        var handler = new FakeHttpHandler(
            (HttpStatusCode.OK, Encoding.UTF8.GetBytes("IGNORED")),
            (HttpStatusCode.OK, Encoding.UTF8.GetBytes("FULL")));
        var http = new HttpClient(handler);
        try
        {
            await RuntimeBootstrap.DownloadResumableAsync(http, "https://src/file", dest, CancellationToken.None);
            // 200（服务端不支持 Range/文件已变）：重头下载覆盖，绝不追加错位字节
            Assert.Equal("FULL", File.ReadAllText(dest));
            Assert.Equal(new string?[] { "bytes=4-", null }, handler.Ranges);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadResumable_ExistingAnd416_RestartsFromZero()
    {
        var dest = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(dest, "AAAA");
        var handler = new FakeHttpHandler(
            (HttpStatusCode.RequestedRangeNotSatisfiable, Array.Empty<byte>()),
            (HttpStatusCode.OK, Encoding.UTF8.GetBytes("FULL")));
        var http = new HttpClient(handler);
        try
        {
            await RuntimeBootstrap.DownloadResumableAsync(http, "https://src/file", dest, CancellationToken.None);
            // 416（Range 不可满足=文件疑似已变）：重头下载兜底，正确性由后续 SHA256 兜底
            Assert.Equal("FULL", File.ReadAllText(dest));
            Assert.Equal(new string?[] { "bytes=4-", null }, handler.Ranges);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadResumable_NoExisting_SendsPlainGet()
    {
        var dest = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        var handler = new FakeHttpHandler((HttpStatusCode.OK, Encoding.UTF8.GetBytes("FULL")));
        var http = new HttpClient(handler);
        try
        {
            await RuntimeBootstrap.DownloadResumableAsync(http, "https://src/file", dest, CancellationToken.None);
            Assert.Equal("FULL", File.ReadAllText(dest));
            Assert.Equal(new string?[] { null }, handler.Ranges);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadWithFallback_NonNetworkBug_NotSwallowed()
    {
        // B2：编程 bug（非网络/IO 异常）不得被伪造成「源失败」，应原样上抛 fail loud
        var dest = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, destPath, ct) => throw new InvalidOperationException("programmer bug"),
                FetchTextAsync: (url, ct) => Task.FromResult(string.Empty),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) => Task.FromResult((0, string.Empty, string.Empty)),
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => RuntimeBootstrap.DownloadWithFallbackAsync(
                    new[] { "https://a", "https://b" }, dest, hooks, CancellationToken.None));
        }
        finally
        {
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }
        }
    }

    // —— batch 3 · 原子 staging + 锁重试 ——

    [Fact]
    public void SwapStaging_FreshRuntime_PopulatesRuntimeDir()
    {
        var parent = Path.Combine(Path.GetTempPath(), "swap-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(parent, ".staging-x");
        var runtime = Path.Combine(parent, "runtime");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "node"), "new");
        try
        {
            RuntimeBootstrap.SwapStagingIntoPlace(staging, runtime);
            Assert.True(File.Exists(Path.Combine(runtime, "node")));
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Fact]
    public void SwapStaging_ExistingRuntime_BacksUpAndReplaces()
    {
        var parent = Path.Combine(Path.GetTempPath(), "swap-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(parent, ".staging-x");
        var runtime = Path.Combine(parent, "runtime");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(staging, "node"), "new");
        File.WriteAllText(Path.Combine(runtime, "node"), "old");
        try
        {
            RuntimeBootstrap.SwapStagingIntoPlace(staging, runtime);
            Assert.Equal("new", File.ReadAllText(Path.Combine(runtime, "node")));
            // 旧 runtimeDir 已备份并在成功后清理，不再残留 .backup
            Assert.Empty(Directory.GetDirectories(parent, ".backup-*"));
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Fact]
    public void CleanupStaleStagingDirs_RemovesTransient_KeepsFormalDirs()
    {
        // S1：跨崩溃残留的 .staging-*/.backup-* 被清理，正式/用户目录不受影响
        var parent = Path.Combine(Path.GetTempPath(), "stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(Path.Combine(parent, ".staging-dead"));
        Directory.CreateDirectory(Path.Combine(parent, ".backup-dead"));
        Directory.CreateDirectory(Path.Combine(parent, "runtime"));
        try
        {
            RuntimeBootstrap.CleanupStaleStagingDirs(parent);
            Assert.False(Directory.Exists(Path.Combine(parent, ".staging-dead")));
            Assert.False(Directory.Exists(Path.Combine(parent, ".backup-dead")));
            Assert.True(Directory.Exists(Path.Combine(parent, "runtime")));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void WithLockRetry_RetriesOnIOException_ThenSucceeds()
    {
        var attempts = 0;
        RuntimeBootstrap.WithLockRetry(() =>
        {
            attempts++;
            if (attempts <= 2)
            {
                throw new IOException("transient lock");
            }
        });
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void WithLockRetry_Exhausts_Throws()
    {
        var attempts = 0;
        Assert.Throws<IOException>(() => RuntimeBootstrap.WithLockRetry(() =>
        {
            attempts++;
            throw new IOException("persistent lock");
        }));
        Assert.Equal(RuntimeBootstrap.FileLockRetryCount, attempts);
    }

    // —— 测试用假 HTTP 处理器：按序出响应、记录 Range/URL ——
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, byte[] Body)> _responses;

        public FakeHttpHandler(params (HttpStatusCode Status, byte[] Body)[] responses) =>
            _responses = new Queue<(HttpStatusCode, byte[])>(responses);

        public List<string> Urls { get; } = new();
        public List<string?> Ranges { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            Ranges.Add(request.Headers.Range?.ToString());
            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, Array.Empty<byte>());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }
}

/// <summary>
/// 真实网络 E2E（env 门控 <c>DSH_TEST_BOOTSTRAP_E2E=1</c> 才执行，否则自跳过）：真下载钉版
/// Node + 官方 SHASUMS256 校验 + tar 解压 + npm 安装 dsh@latest + 产物验证。验证生产 hooks
/// 主链（全新下载走普通 GET、原子落盘、子进程）。断点续传（206/416）与多源回落分支为纯单测
/// 覆盖（本 E2E 为全新 runtimeDir + 官方源成功，不触发该二分支）。跑一次约 1-3 分钟、依赖外网
/// 与 npm 可达——CI 不默认跑。
/// </summary>
public class RuntimeBootstrapE2ETests
{
    [Fact]
    public async Task RunAsync_RealNetwork_InstallsDsh()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_BOOTSTRAP_E2E") != "1")
        {
            return; // 门控自跳过（与 HarnessRuntimeHostTests 的 DSH_TEST_E2E 同款模式）
        }

        var runtimeDir = Path.Combine(Path.GetTempPath(), "boot-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions(),
                runtimeDir,
                p => Console.WriteLine($"[e2e] {p.Step}: {p.Message}"),
                RuntimeBootstrap.CreateDefaultHooks(Console.WriteLine),
                CancellationToken.None);

            Assert.True(outcome.Success, outcome.Error);
            Assert.NotNull(outcome.Runtime);
        }
        finally
        {
            if (Directory.Exists(runtimeDir))
            {
                Directory.Delete(runtimeDir, recursive: true);
            }
        }
    }
}

/// <summary>子进程文本流 UTF-8 单点契约（ADR online-first-unbundled-runtime 踩坑约束）。</summary>
public class Utf8TextStreamsTests
{
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

    [Fact]
    public void Router_RetryCommand_SignalsGate()
    {
        var gate = new RuntimeBootstrapGate();
        var router = new BootstrapCommandRouter(gate);
        Assert.True(router.CanRoute("desktop.bootstrap.retry"));
        Assert.False(router.CanRoute("desktop.recovery.exit"));

        var result = router.RouteAsync("desktop.bootstrap.retry", default, null!, CancellationToken.None);
        Assert.True(result.IsCompletedSuccessfully);
        Assert.True(gate.IsSignaled);
    }

    [Fact]
    public async Task Router_UnknownCommand_ThrowsRynCommandNotFound()
    {
        var router = new BootstrapCommandRouter(new RuntimeBootstrapGate());
        await Assert.ThrowsAsync<RynCommandNotFoundException>(
            () => router.RouteAsync("desktop.unknown", default, null!, CancellationToken.None).AsTask());
    }
}

/// <summary>RuntimeBootstrapOptions 配置装载：节缺失全默认、合法节逐项覆盖、坏 JSON fail-safe。</summary>
public class RuntimeBootstrapOptionsTests
{
    [Fact]
    public void Load_MissingSection_ReturnsDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "opts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"Update":{}}""");
            var options = RuntimeBootstrapOptions.Load(dir);
            Assert.Equal("24.20.0", options.NodeVersion);
            Assert.Equal("@deepseek-ai/dsh@latest", options.DshSpec);
            Assert.Equal(22, options.MinimumLocalNodeMajor);
            Assert.Equal("https://cdn.npmmirror.com/binaries/node", options.NodeMirrorBaseUrl);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_ValidSection_Overrides()
    {
        var dir = Path.Combine(Path.GetTempPath(), "opts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
                {"RuntimeBootstrap":{"NodeVersion":"22.0.0","DshSpec":"@deepseek-ai/dsh@0.1.1-rc.2","MinimumLocalNodeMajor":20,"StepTimeoutMinutes":5,"NodeDistBaseUrl":"https://mirror.example/dist","NodeMirrorBaseUrl":"https://cdn.example.com/node"}}
                """);
            var options = RuntimeBootstrapOptions.Load(dir);
            Assert.Equal("22.0.0", options.NodeVersion);
            Assert.Equal("@deepseek-ai/dsh@0.1.1-rc.2", options.DshSpec);
            Assert.Equal(20, options.MinimumLocalNodeMajor);
            Assert.Equal(5, options.StepTimeoutMinutes);
            Assert.Equal("https://mirror.example/dist", options.NodeDistBaseUrl);
            Assert.Equal("https://cdn.example.com/node", options.NodeMirrorBaseUrl);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_BrokenJson_FailsSafeToDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "opts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{not json");
            var options = RuntimeBootstrapOptions.Load(dir);
            Assert.Equal("24.20.0", options.NodeVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
