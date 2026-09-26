namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>
/// npm 全局 bin 暴露（P1 根因修复）：装机 node 的 npm 前缀确定性解析并补进进程 PATH，
/// 预装 node + 自定义前缀不再 VerifyDsh 注定失败。fake hooks 注入，无网络无真 npm。
/// </summary>
[Collection("bootstrap-node-env")]
public class NpmPrefixExposeTests
{
    /// <summary>构造仅回答 prefix 查询的 fake hooks（其余命令抛，避免测试与实现外行为耦合）。</summary>
    private static RuntimeBootstrapHooks PrefixHooks(string? prefixOutput, int exit = 0)
    {
        return new RuntimeBootstrapHooks(
            DownloadFileAsync: (url, dest, ct) => Task.CompletedTask,
            FetchTextAsync: (url, ct) => Task.FromResult(string.Empty),
            ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
            RunProcessAsync: (exe, args, ct) =>
            {
                string joined = string.Join(' ', args);
                if (joined.Contains("config get prefix", StringComparison.Ordinal))
                {
                    return Task.FromResult((exit, prefixOutput ?? string.Empty, string.Empty));
                }

                throw new InvalidOperationException($"unexpected call: {exe} {joined}");
            },
            ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((null, null)));
    }

    private static NodeResult FakeNode() => new("/fake/node", "/fake/npm-cli.js");

    /// <summary>前缀存在 → 返回 bin 目录（Unix `prefix/bin`、Windows 前缀根）。</summary>
    [Fact]
    public async Task ResolveNpmGlobalBinDir_ExistingPrefix_ReturnsBinDir()
    {
        string root = Directory.CreateTempSubdirectory("npm-prefix").FullName;
        try
        {
            string expected = OperatingSystem.IsWindows() ? root : Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            string? binDir = await RuntimeBootstrap.ResolveNpmGlobalBinDirAsync(
                FakeNode(), PrefixHooks(root + Environment.NewLine), CancellationToken.None);
            Assert.Equal(expected, binDir);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>前缀不存在 → null（沿用既有 PATH，走既有指引失败）。</summary>
    [Fact]
    public async Task ResolveNpmGlobalBinDir_MissingPrefix_ReturnsNull()
    {
        string? binDir = await RuntimeBootstrap.ResolveNpmGlobalBinDirAsync(
            FakeNode(), PrefixHooks(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), CancellationToken.None);
        Assert.Null(binDir);
    }

    /// <summary>查询非零退出 / 空输出 → null。</summary>
    [Theory]
    [InlineData(1, "/fake/prefix")]
    [InlineData(0, "   ")]
    public async Task ResolveNpmGlobalBinDir_BadQuery_ReturnsNull(int exit, string output)
    {
        string? binDir = await RuntimeBootstrap.ResolveNpmGlobalBinDirAsync(
            FakeNode(), PrefixHooks(output, exit), CancellationToken.None);
        Assert.Null(binDir);
    }

    /// <summary>端到端：RunAsync 成功路径把解析出的 npm bin 补进进程 PATH（前后恢复现场）。</summary>
    [Fact]
    public async Task RunAsync_ExposesNpmBinDirOnPath()
    {
        string root = Directory.CreateTempSubdirectory("npm-expose").FullName;
        string binDir = OperatingSystem.IsWindows() ? root : Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        string savedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        try
        {
            var hooks = new RuntimeBootstrapHooks(
                DownloadFileAsync: (url, dest, ct) => Task.CompletedTask,
                FetchTextAsync: (url, ct) => Task.FromResult(string.Empty),
                ExtractArchiveAsync: (archive, destDir, ct) => Task.CompletedTask,
                RunProcessAsync: (exe, args, ct) =>
                {
                    string joined = string.Join(' ', args);
                    if (joined.Contains("config get prefix", StringComparison.Ordinal))
                    {
                        return Task.FromResult((0, root + Environment.NewLine, string.Empty));
                    }

                    if (joined.Contains("install", StringComparison.Ordinal))
                    {
                        return Task.FromResult((0, string.Empty, string.Empty));
                    }

                    return Task.FromResult((0, "0.1.7-alpha.2" + Environment.NewLine, string.Empty));
                },
                ProbeLocalNodeAsync: ct => Task.FromResult<(string?, string?)>((Path.Combine("/fake", "node"), Path.Combine("/fake", "npm-cli.js"))));
            BootstrapOutcome outcome = await RuntimeBootstrap.RunAsync(
                new RuntimeBootstrapOptions { DshSpec = "@deepseek-ai/dsh@alpha" },
                _ => { },
                hooks,
                english: false,
                CancellationToken.None);
            Assert.True(outcome.Success, outcome.Error);
            Assert.Contains(
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator),
                p => string.Equals(p, binDir, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Directory.Delete(root, recursive: true);
        }
    }
}
