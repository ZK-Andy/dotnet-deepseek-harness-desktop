using DeepSeek.Harness.Desktop.Services.Tray;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主命令路由：<c>desktop.recovery.exit</c>——恢复页「退出应用」按钮。语义与托盘退出
/// 一致：先 <see cref="CloseGate.ApproveExit"/> 再 Close（hide-to-tray 拦截下不批准的
/// Close 会被吞成隐藏，用户想退出却得到一个看不见的窗口）。设计镜像
/// <c>DesktopTrayCommandRouter</c>：持有闸门自行批准，顺序即契约（记序 fake 测试钉住，
/// ADR diag-masking-and-recovery-page）。
/// </summary>
public sealed class RecoveryCommandRouter : ICommandRouter
{
	/// <summary>本路由响应的命令名。</summary>
	public const string CommandName = "desktop.recovery.exit";

	private readonly Action _closeWindow;
	private readonly CloseGate _closeGate;
	private readonly Action<string>? _log;

	/// <summary>创建路由。<paramref name="closeWindow"/> 为关窗动作；批准走持有的闸门。</summary>
	public RecoveryCommandRouter(Action closeWindow, CloseGate closeGate, Action<string>? log = null)
	{
		_closeWindow = closeWindow;
		_closeGate = closeGate;
		_log = log;
	}

	/// <inheritdoc />
	public bool CanRoute(string command) => string.Equals(command, CommandName, StringComparison.Ordinal);

	/// <inheritdoc />
	public ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken cancellationToken)
	{
		if (!CanRoute(command))
		{
			throw new RynCommandNotFoundException(command);
		}

		// 先批准再关窗：与托盘退出同一条顺序契约
		_closeGate.ApproveExit();
		try
		{
			_closeWindow();
			_log?.Invoke("[host] 恢复页请求退出：已放行关窗");
		}
		catch (Exception ex)
		{
			_log?.Invoke($"[host] 恢复页退出关窗失败：{ex.Message}");
		}

		return ValueTask.FromResult("{}");
	}
}
