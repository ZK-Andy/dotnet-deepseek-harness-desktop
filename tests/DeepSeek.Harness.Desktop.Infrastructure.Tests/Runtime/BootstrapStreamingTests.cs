using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>
/// 引导供应链输出透传（ADR bootstrap-provisioning-observability）：共享行泵逐行转发/累积/截断、
/// 默认 hooks 的子进程执行走流式捕获（版本行既透传日志又累积在 stdout）。
/// 行泵实现复用 <c>PluginProcessRunner.PumpAsync</c>（R1 简化统一），此处只锁契约。
/// </summary>
[Collection("bootstrap-node-env")]
public class BootstrapStreamingTests
{
    /// <summary>行泵逐行转发并累积；超长行截断（上限 + 省略号）后转发与累积一致。</summary>
    [Fact]
    public async Task PumpLines_ForwardsAccumulatesAndTruncates()
    {
        int max = RuntimeBootstrap.MaxStreamLineChars;
        string longLine = new('x', max + 50);
        string expectedTruncated = longLine[..max] + "…";
        using var reader = new StringReader($"first\n{longLine}\nlast\n");
        var sb = new StringBuilder();
        List<string> forwarded = [];
        await global::DeepSeek.Harness.Desktop.Infrastructure.Plugins.PluginProcessRunner.PumpAsync(reader, sb, forwarded.Add, CancellationToken.None, max);
        Assert.Equal(["first", expectedTruncated, "last"], forwarded);
        string accumulated = sb.ToString();
        Assert.Contains("first", accumulated);
        Assert.Contains(expectedTruncated, accumulated);
        Assert.Contains("last", accumulated);
    }

    /// <summary>行泵空回调只累积不转发；不限长时原文累积。</summary>
    [Fact]
    public async Task PumpLines_NullCallback_AccumulatesOnly()
    {
        using var reader = new StringReader("a\nb\n");
        var sb = new StringBuilder();
        await global::DeepSeek.Harness.Desktop.Infrastructure.Plugins.PluginProcessRunner.PumpAsync(reader, sb, null, CancellationToken.None);
        Assert.Equal($"a{Environment.NewLine}b{Environment.NewLine}", sb.ToString());
    }

    /// <summary>默认 hooks 的子进程执行走流式捕获：`dotnet --version` 的版本行既透传到日志又累积在 stdout。</summary>
    [Fact]
    public async Task DefaultHooks_RunProcess_StreamsLinesToLog()
    {
        List<string> lines = [];
        RuntimeBootstrapHooks hooks = RuntimeBootstrap.CreateDefaultHooks(lines.Add, english: false, new RuntimeBootstrapOptions());
        (int exit, string? stdout, string? _) = await hooks.RunProcessAsync("dotnet", ["--version"], CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.NotNull(stdout);
        string version = stdout.Trim();
        Assert.Matches(@"^\d+\.\d+", version);
        Assert.Contains(lines, l => l.Contains("run:") && l.Contains("dotnet"));
        Assert.Contains(lines, l => l.Contains(version));
    }
}
