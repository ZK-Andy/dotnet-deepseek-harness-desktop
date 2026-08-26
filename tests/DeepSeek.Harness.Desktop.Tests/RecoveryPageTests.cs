using DeepSeek.Harness.Desktop.Services;
using DeepSeek.Harness.Desktop.Services.Tray;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>恢复页构建契约（ADR diag-masking-and-recovery-page）：数据只经 JSON+textContent
/// 落地，绝不进 innerHTML；两个动作按钮与 stderr 容器必须存在。</summary>
public class RecoveryPageBuilderTests
{
	[Fact]
	public void Script_ContainsSkeleton_Buttons_AndPayload()
	{
		var script = RecoveryPageBuilder.BuildScript("运行时进程意外退出", new[] { "line-1", "line-2" });

		Assert.Contains("ddc-reason", script);
		Assert.Contains("ddc-tail", script);
		Assert.Contains("ddc-export", script);
		Assert.Contains("ddc-exit", script);
		Assert.Contains("desktop.diagnostics.export", script);
		Assert.Contains("desktop.recovery.exit", script);
		Assert.Contains("\\u8FD0\\u884C\\u65F6", script); // reason 经 JSON 序列化（非 ASCII 转义）
	}

	[Fact]
	public void StderrLines_Escaped_NotRawHtml_InjectionSafe()
	{
		const string evil = "</div><script>alert(1)</script><img src=x onerror=alert(2)>";
		var script = RecoveryPageBuilder.BuildScript("reason", new[] { evil });

		// 恶意行必须以 JSON 字符串转义形态存在（< → \u003C），不存在裸 <script>
		Assert.DoesNotContain("<script>alert(1)</script>", script);
		Assert.Contains("u003C", script, StringComparison.OrdinalIgnoreCase);

		// 数据回填走 textContent，无任何针对 tail 的 innerHTML 拼接
		Assert.Contains("d.textContent=l", script.Replace("\r", "").Replace("\n", ""));
	}

	[Fact]
	public void EmptyTail_TailHidden_NoCrash()
	{
		var script = RecoveryPageBuilder.BuildScript("reason", Array.Empty<string>());

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
			approveExit: () =>
			{
				calls.Add("approve");
				gate.ApproveExit();
			},
			closeWindow: () => calls.Add(gate.ShouldCancelClose ? "close:locked" : "close:released"),
			closeGate: gate);
		return (router, calls);
	}

	[Fact]
	public async Task Exit_ApprovesGateBeforeClosing_OrderContract()
	{
		var gate = new CloseGate();
		var (router, calls) = MakeRouter(gate);

		var frame = await router.RouteAsync(
			Services.RecoveryCommandRouter.CommandName,
			System.Text.Encoding.UTF8.GetBytes("{}"),
			null!,
			CancellationToken.None);

		Assert.Equal(new[] { "approve", "close:released" }, calls);
		Assert.False(gate.ShouldCancelClose);
		Assert.Equal("{}", frame);
	}

	[Fact]
	public async Task UnknownCommand_Throws()
	{
		var (router, _) = MakeRouter(new CloseGate());

		await Assert.ThrowsAsync<Ryn.Ipc.RynCommandNotFoundException>(() =>
			router.RouteAsync("desktop.other", ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None).AsTask());
	}
}
