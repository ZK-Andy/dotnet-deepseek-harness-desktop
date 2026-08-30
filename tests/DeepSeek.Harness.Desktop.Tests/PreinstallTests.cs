using System.IO;
using System.Text;
using System.Text.Json;
using DeepSeek.Harness.Desktop;
using DeepSeek.Harness.Desktop.Services;
using Ryn.Ipc;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>插件引导（ADR reference-alignment 批次二）的决策闸门、命令路由、preset 判定与帧形状。</summary>
public class PreinstallTests
{
    // —— PreinstallChoiceGate ——

    [Fact]
    public void Gate_NotDecided_Initially()
    {
        var gate = new PreinstallChoiceGate();
        Assert.False(gate.IsDecided);
    }

    [Fact]
    public async Task Gate_SetInstall_CompletesChoiceWithInstall()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Install);
        Assert.True(gate.IsDecided);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    [Fact]
    public async Task Gate_SetSkip_CompletesChoiceWithSkip()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Skip);
        Assert.True(gate.IsDecided);
        Assert.Equal(PreinstallChoice.Skip, await gate.Choice);
    }

    [Fact]
    public async Task Gate_SecondSet_Ignored()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Install);
        gate.Set(PreinstallChoice.Skip);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    [Fact]
    public void Gate_Reset_ClearsDecision()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Install);
        gate.Reset();
        Assert.False(gate.IsDecided);
    }

    // —— PreinstallCommandRouter ——

    [Fact]
    public void Router_CanRoute_MatchesCommand()
    {
        var router = new PreinstallCommandRouter(new PreinstallChoiceGate());
        Assert.True(router.CanRoute(PreinstallCommandRouter.CommandName));
        Assert.False(router.CanRoute("desktop.bootstrap.retry"));
    }

    [Fact]
    public async Task Router_Install_SetsGateInstall()
    {
        var gate = new PreinstallChoiceGate();
        var router = new PreinstallCommandRouter(gate);
        await router.RouteAsync("desktop.preinstall.choose", Args("""{"action":"install"}"""), null!, default);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    [Fact]
    public async Task Router_Skip_SetsGateSkip()
    {
        var gate = new PreinstallChoiceGate();
        var router = new PreinstallCommandRouter(gate);
        await router.RouteAsync("desktop.preinstall.choose", Args("""{"action":"skip"}"""), null!, default);
        Assert.Equal(PreinstallChoice.Skip, await gate.Choice);
    }

    [Fact]
    public async Task Router_UnknownAction_DefaultsToInstall()
    {
        var gate = new PreinstallChoiceGate();
        var router = new PreinstallCommandRouter(gate);
        await router.RouteAsync("desktop.preinstall.choose", Args("""{"action":"weird"}"""), null!, default);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    [Fact]
    public async Task Router_EmptyArgs_DefaultsToInstall()
    {
        var gate = new PreinstallChoiceGate();
        var router = new PreinstallCommandRouter(gate);
        await router.RouteAsync("desktop.preinstall.choose", ReadOnlyMemory<byte>.Empty, null!, default);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    [Fact]
    public async Task Router_UnknownCommand_Throws()
    {
        var router = new PreinstallCommandRouter(new PreinstallChoiceGate());
        await Assert.ThrowsAsync<RynCommandNotFoundException>(
            () => router.RouteAsync("desktop.preinstall.decide", Args("""{"action":"install"}"""), null!, default).AsTask());
    }

    private static ReadOnlyMemory<byte> Args(string json) => new(Encoding.UTF8.GetBytes(json));

    // —— PresetPluginCatalog ——

    private static string WriteProfileJson(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "preinstall-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Pending_ReturnsMarket_WhenFileMissing()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Equal(["dshmarket"], PresetPluginCatalog.PendingForFirstBoot(missing));
    }

    [Fact]
    public void Pending_ReturnsMarket_WhenNotInstalled()
    {
        string p = WriteProfileJson("""{"dependencies":{},"dsh":{"profile":{"bundles":["web-app"]}}}""");
        try
        {
            Assert.Equal(["dshmarket"], PresetPluginCatalog.PendingForFirstBoot(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Pending_ReturnsEmpty_WhenMarketInstalled()
    {
        string p = WriteProfileJson("""{"dependencies":{"dshmarket":"^1.36.0"},"dsh":{"profile":{"bundles":["dshmarket","web-app"]}}}""");
        try
        {
            Assert.Empty(PresetPluginCatalog.PendingForFirstBoot(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Pending_ReturnsMarket_WhenDepButNoBundle()
    {
        // registry 安装后 bundles 未补写（异常路径）：按未就位处理，引导页重新呈现
        string p = WriteProfileJson("""{"dependencies":{"dshmarket":"^1.36.0"},"dsh":{"profile":{"bundles":["web-app"]}}}""");
        try
        {
            Assert.Equal(["dshmarket"], PresetPluginCatalog.PendingForFirstBoot(p));
        }
        finally { File.Delete(p); }
    }

    // —— PreinstallFrame 形状 ——

    [Fact]
    public void Frame_Decision_ExactShape()
    {
        Assert.Equal("""{"kind":"decision","plugins":["dshmarket"]}""",
            JsonSerializer.Serialize(new PreinstallFrame("decision", Plugins: ["dshmarket"]), AppJsonContext.Default.PreinstallFrame));
    }

    [Fact]
    public void Frame_Log_ExactShape()
    {
        Assert.Equal("""{"kind":"log","line":"Progress 42%"}""",
            JsonSerializer.Serialize(new PreinstallFrame("log", Line: "Progress 42%"), AppJsonContext.Default.PreinstallFrame));
    }

    [Fact]
    public void Frame_Done_ExactShape()
    {
        Assert.Equal("""{"kind":"done","action":"install","ok":true,"message":"complete"}""",
            JsonSerializer.Serialize(new PreinstallFrame("done", Action: "install", Ok: true, Message: "complete"), AppJsonContext.Default.PreinstallFrame));
    }

    [Fact]
    public void Frame_Done_Skip_OmitsOk()
    {
        // skip 帧无 ok（Ok 为 null）：仅 kind + action
        Assert.Equal("""{"kind":"done","action":"skip"}""",
            JsonSerializer.Serialize(new PreinstallFrame("done", Action: "skip"), AppJsonContext.Default.PreinstallFrame));
    }

    [Fact]
    public void Frame_HostileLine_EscapesForJs()
    {
        const string hostile = "</script>init";
        string json = JsonSerializer.Serialize(new PreinstallFrame("log", Line: hostile), AppJsonContext.Default.PreinstallFrame);
        Assert.DoesNotContain('<', json);
        Assert.Equal(hostile, JsonDocument.Parse(json).RootElement.GetProperty("line").GetString());
    }

    [Fact]
    public void Frame_ChineseMessage_RoundTrips_WithEscapedJson()
    {
        var frame = new PreinstallFrame("done", Action: "install", Ok: false, Message: "安装未成功");
        string json = JsonSerializer.Serialize(frame, AppJsonContext.Default.PreinstallFrame);
        Assert.Contains("\\u", json);
        Assert.Equal("安装未成功", JsonDocument.Parse(json).RootElement.GetProperty("message").GetString());
    }

    // —— 流式行泵（RunProcessStreamingAsync 的核心：逐行转发 + 累积）——

    [Fact]
    public async Task PumpAsync_ForwardsEachLine_AndAccumulates()
    {
        var sb = new StringBuilder();
        var seen = new List<string>();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n"));
        using var reader = new StreamReader(ms);
        await Program.PumpAsync(reader, sb, seen.Add, CancellationToken.None);
        Assert.Equal("alpha\nbeta\ngamma\n", sb.ToString());
        Assert.Equal(["alpha", "beta", "gamma"], seen);
    }

    [Fact]
    public async Task PumpAsync_CapturesLastLineWithoutTrailingNewline()
    {
        var sb = new StringBuilder();
        var seen = new List<string>();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("a\nb"));
        using var reader = new StreamReader(ms);
        await Program.PumpAsync(reader, sb, seen.Add, CancellationToken.None);
        Assert.Equal(["a", "b"], seen);
    }
}
