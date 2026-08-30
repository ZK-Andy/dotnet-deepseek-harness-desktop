using DeepSeek.Harness.Desktop.Services;
using DeepSeek.Harness.Desktop.Services.Tray;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>恢复页构建契约（ADR diag-masking-and-recovery-page）：数据只经 JSON+textContent
/// 落地，绝不进 innerHTML；两个动作按钮与 stderr 容器必须存在。</summary>
public class RecoveryPageBuilderTests
{
    /// <summary>验证生成的恢复页脚本含骨架元素 id（ddc-reason/tail/export/exit）、两个 IPC 命令与 JSON 转义后的 reason 载荷。</summary>
    [Fact]
    public void Script_ContainsSkeleton_Buttons_AndPayload()
    {
        string script = RecoveryPageBuilder.BuildScript("运行时进程意外退出", new[] { "line-1", "line-2" });

        Assert.Contains("ddc-reason", script);
        Assert.Contains("ddc-tail", script);
        Assert.Contains("ddc-export", script);
        Assert.Contains("ddc-exit", script);
        Assert.Contains("desktop.diagnostics.export", script);
        Assert.Contains("desktop.recovery.exit", script);
        Assert.Contains("\\u8FD0\\u884C\\u65F6", script); // reason 经 JSON 序列化（非 ASCII 转义）
    }

    /// <summary>验证恶意 stderr 行以 JSON 转义形态（&lt; → \u003C）落地、无裸 &lt;script&gt;，且数据回填走 textContent 而非 innerHTML。</summary>
    [Fact]
    public void StderrLines_Escaped_NotRawHtml_InjectionSafe()
    {
        const string evil = "</div><script>alert(1)</script><img src=x onerror=alert(2)>";
        string script = RecoveryPageBuilder.BuildScript("reason", new[] { evil });

        // 恶意行必须以 JSON 字符串转义形态存在（< → \u003C），不存在裸 <script>
        Assert.DoesNotContain("<script>alert(1)</script>", script);
        Assert.Contains("u003C", script, StringComparison.OrdinalIgnoreCase);

        // 数据回填走 textContent，无任何针对 tail 的 innerHTML 拼接
        Assert.Contains("d.textContent=l", script.Replace("\r", "").Replace("\n", ""));
    }

    /// <summary>验证空 stderr 时脚本仍含「tail 为空则隐藏」的分支逻辑，不崩溃。</summary>
    [Fact]
    public void EmptyTail_TailHidden_NoCrash()
    {
        string script = RecoveryPageBuilder.BuildScript("reason", Array.Empty<string>());

        Assert.Contains("D.tail&&D.tail.length", script);
    }
}

/// <summary>恢复页退出路由：与托盘退出同一条顺序契约——先批准闸门再关窗。</summary>
public class RecoveryCommandRouterTests
{
    private static (RecoveryCommandRouter Router, List<string> Calls) MakeRouter(CloseGate gate)
    {
        var calls = new List<string>();
        var router = new Services.RecoveryCommandRouter(
            closeWindow: () => calls.Add(gate.ShouldCancelClose ? "close:locked" : "close:released"),
            closeGate: gate,
            log: _ => calls.Add("log"));
        return (router, calls);
    }

    /// <summary>验证退出命令先放行 CloseGate 再执行关窗的顺序契约（与托盘退出同款取证手法）。</summary>
    [Fact]
    public async Task Exit_ApprovesGateBeforeClosing_OrderContract()
    {
        var gate = new CloseGate();
        (RecoveryCommandRouter? router, List<string>? calls) = MakeRouter(gate);

        string frame = await router.RouteAsync(
            Services.RecoveryCommandRouter.CommandName,
            System.Text.Encoding.UTF8.GetBytes("{}"),
            null!,
            CancellationToken.None);

        // close 时闸门已放行 = 批准确实先于 Close 发生（托盘同款取证手法）
        Assert.Equal(new[] { "close:released", "log" }, calls);
        Assert.False(gate.ShouldCancelClose);
        Assert.Equal("{}", frame);
    }

    /// <summary>验证路由未注册命令 desktop.other 抛 RynCommandNotFoundException。</summary>
    [Fact]
    public async Task UnknownCommand_Throws()
    {
        (RecoveryCommandRouter? router, List<string> _) = MakeRouter(new CloseGate());

        await Assert.ThrowsAsync<Ryn.Ipc.RynCommandNotFoundException>(() =>
            router.RouteAsync("desktop.other", ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None).AsTask());
    }
}
