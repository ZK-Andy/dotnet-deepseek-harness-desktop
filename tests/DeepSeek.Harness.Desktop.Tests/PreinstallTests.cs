using System.Text;
using System.Text.Json;
using DeepSeek.Harness.Desktop.Services;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>插件引导（ADR reference-alignment 批次二）的决策闸门、命令路由、preset 判定与帧形状。</summary>
public class PreinstallTests
{
    // —— PreinstallChoiceGate ——

    /// <summary>验证 PreinstallChoiceGate 初始未决策（IsDecided=false）：首个 Set 之前闸门须处于未拍板态。</summary>
    [Fact]
    public void Gate_NotDecided_Initially()
    {
        var gate = new PreinstallChoiceGate();
        Assert.False(gate.IsDecided);
    }

    /// <summary>验证 Set(Install) 使闸门转为已决策，且等待中的 Choice 返回 Install。</summary>
    [Fact]
    public async Task Gate_SetInstall_CompletesChoiceWithInstall()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Install);
        Assert.True(gate.IsDecided);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    /// <summary>验证 Set(Skip) 使闸门转为已决策，且等待中的 Choice 返回 Skip。</summary>
    [Fact]
    public async Task Gate_SetSkip_CompletesChoiceWithSkip()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Skip);
        Assert.True(gate.IsDecided);
        Assert.Equal(PreinstallChoice.Skip, await gate.Choice);
    }

    /// <summary>验证一次拍板即锁定：先 Install 再 Set(Skip)，Choice 仍为 Install，第二次 Set 被忽略。</summary>
    [Fact]
    public async Task Gate_SecondSet_Ignored()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Install);
        gate.Set(PreinstallChoice.Skip);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    /// <summary>验证 Reset 清除已拍板决策，闸门回到未决策态，可重新走选择流程。</summary>
    [Fact]
    public void Gate_Reset_ClearsDecision()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Install);
        gate.Reset();
        Assert.False(gate.IsDecided);
    }

    // —— PreinstallCommandRouter ——

    /// <summary>验证路由只接受 own 命令 desktop.preinstall.choose，其它命令（如 desktop.bootstrap.retry）不可路由。</summary>
    [Fact]
    public void Router_CanRoute_MatchesCommand()
    {
        var router = new PreinstallCommandRouter(new PreinstallChoiceGate());
        Assert.True(router.CanRoute(PreinstallCommandRouter.CommandName));
        Assert.False(router.CanRoute("desktop.bootstrap.retry"));
    }

    /// <summary>验证 {"action":"install"} 载荷经 RouteAsync 落到闸门的 Install 决策。</summary>
    [Fact]
    public async Task Router_Install_SetsGateInstall()
    {
        var gate = new PreinstallChoiceGate();
        var router = new PreinstallCommandRouter(gate);
        await router.RouteAsync("desktop.preinstall.choose", Args("""{"action":"install"}"""), null!, default);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    /// <summary>验证 {"action":"skip"} 载荷经 RouteAsync 落到闸门的 Skip 决策。</summary>
    [Fact]
    public async Task Router_Skip_SetsGateSkip()
    {
        var gate = new PreinstallChoiceGate();
        var router = new PreinstallCommandRouter(gate);
        await router.RouteAsync("desktop.preinstall.choose", Args("""{"action":"skip"}"""), null!, default);
        Assert.Equal(PreinstallChoice.Skip, await gate.Choice);
    }

    /// <summary>验证未知 action 值（weird）按默认 Install 处理，而非报错或卡死。</summary>
    [Fact]
    public async Task Router_UnknownAction_DefaultsToInstall()
    {
        var gate = new PreinstallChoiceGate();
        var router = new PreinstallCommandRouter(gate);
        await router.RouteAsync("desktop.preinstall.choose", Args("""{"action":"weird"}"""), null!, default);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    /// <summary>验证空参数体（无 JSON）与未知 action 同路，默认决策为 Install。</summary>
    [Fact]
    public async Task Router_EmptyArgs_DefaultsToInstall()
    {
        var gate = new PreinstallChoiceGate();
        var router = new PreinstallCommandRouter(gate);
        await router.RouteAsync("desktop.preinstall.choose", ReadOnlyMemory<byte>.Empty, null!, default);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    /// <summary>验证路由到未注册命令 desktop.preinstall.decide 抛 RynCommandNotFoundException 而非静默吞掉。</summary>
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

    /// <summary>验证 profile 文件缺失时首启待装列表为 dshmarket（按全新环境处理）而非报错。</summary>
    [Fact]
    public void Pending_ReturnsMarket_WhenFileMissing()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Equal(["dshmarket"], PresetPluginCatalog.PendingForFirstBoot(missing));
    }

    /// <summary>验证 dependencies 与 bundles 均未含 dshmarket 时判定其为待装插件。</summary>
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

    /// <summary>验证 dependencies 与 bundles 均已就位 dshmarket 时首启待装列表为空。</summary>
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

    /// <summary>验证「registry 已装但 bundles 未补写」的异常路径按未就位处理，引导页重新呈现 dshmarket。</summary>
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

    /// <summary>验证 decision 帧 JSON 序列化为紧凑形态时字段顺序与大小写精确为 {"kind":"decision","plugins":[...]}。</summary>
    [Fact]
    public void Frame_Decision_ExactShape()
    {
        Assert.Equal("""{"kind":"decision","plugins":["dshmarket"]}""",
            JsonSerializer.Serialize(new PreinstallFrame("decision", Plugins: ["dshmarket"]), AppJsonContext.Default.PreinstallFrame));
    }

    /// <summary>验证 log 帧 JSON 序列化形状精确为 {"kind":"log","line":"..."}。</summary>
    [Fact]
    public void Frame_Log_ExactShape()
    {
        Assert.Equal("""{"kind":"log","line":"Progress 42%"}""",
            JsonSerializer.Serialize(new PreinstallFrame("log", Line: "Progress 42%"), AppJsonContext.Default.PreinstallFrame));
    }

    /// <summary>验证 done(install/ok:true) 帧序列化为 kind+action+ok+message 四字段的精确形状。</summary>
    [Fact]
    public void Frame_Done_ExactShape()
    {
        Assert.Equal("""{"kind":"done","action":"install","ok":true,"message":"complete"}""",
            JsonSerializer.Serialize(new PreinstallFrame("done", Action: "install", Ok: true, Message: "complete"), AppJsonContext.Default.PreinstallFrame));
    }

    /// <summary>验证 skip 帧的 null Ok 字段被省略：序列化只保留 kind+action 两字段。</summary>
    [Fact]
    public void Frame_Done_Skip_OmitsOk()
    {
        // skip 帧无 ok（Ok 为 null）：仅 kind + action
        Assert.Equal("""{"kind":"done","action":"skip"}""",
            JsonSerializer.Serialize(new PreinstallFrame("done", Action: "skip"), AppJsonContext.Default.PreinstallFrame));
    }

    /// <summary>验证含 &lt;/script&gt; 的日志行经 JSON 转义后无裸 &lt; 落入脚本（XSS 面），且能解析回原值。</summary>
    [Fact]
    public void Frame_HostileLine_EscapesForJs()
    {
        const string hostile = "</script>init";
        string json = JsonSerializer.Serialize(new PreinstallFrame("log", Line: hostile), AppJsonContext.Default.PreinstallFrame);
        Assert.DoesNotContain('<', json);
        Assert.Equal(hostile, JsonDocument.Parse(json).RootElement.GetProperty("line").GetString());
    }

    /// <summary>验证中文消息以 \uXXXX 转义形态序列化（ASCII 安全），且能解析往返回原值。</summary>
    [Fact]
    public void Frame_ChineseMessage_RoundTrips_WithEscapedJson()
    {
        var frame = new PreinstallFrame("done", Action: "install", Ok: false, Message: "安装未成功");
        string json = JsonSerializer.Serialize(frame, AppJsonContext.Default.PreinstallFrame);
        Assert.Contains("\\u", json);
        Assert.Equal("安装未成功", JsonDocument.Parse(json).RootElement.GetProperty("message").GetString());
    }

    // —— 流式行泵（RunProcessStreamingAsync 的核心：逐行转发 + 累积）——

    /// <summary>验证行泵逐行转发（含结尾换行）且 StringBuilder 累积完整输出，行事件与累积内容一致。</summary>
    [Fact]
    public async Task PumpAsync_ForwardsEachLine_AndAccumulates()
    {
        var sb = new StringBuilder();
        var seen = new List<string>();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n"));
        using var reader = new StreamReader(ms);
        await PluginProcessRunner.PumpAsync(reader, sb, seen.Add, CancellationToken.None);
        Assert.Equal("alpha\nbeta\ngamma\n", sb.ToString());
        Assert.Equal(["alpha", "beta", "gamma"], seen);
    }

    /// <summary>验证末行无换行符时仍被完整捕获为一行转发，不丢最后一段输出。</summary>
    [Fact]
    public async Task PumpAsync_CapturesLastLineWithoutTrailingNewline()
    {
        var sb = new StringBuilder();
        var seen = new List<string>();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("a\nb"));
        using var reader = new StreamReader(ms);
        await PluginProcessRunner.PumpAsync(reader, sb, seen.Add, CancellationToken.None);
        Assert.Equal(["a", "b"], seen);
    }
}
