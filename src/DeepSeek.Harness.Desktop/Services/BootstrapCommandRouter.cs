using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 引导页命令路由（Ryn 层 IPC，不依赖 dsh 存活）：<c>desktop.bootstrap.retry</c> 放行重试。
/// 设计镜像 RecoveryCommandRouter——本地 wwwroot 页面可发命令，路由动作经注入委托解耦。
/// </summary>
public sealed class BootstrapCommandRouter : ICommandRouter
{
    /// <summary>重试命令名。</summary>
    public const string CommandName = "desktop.bootstrap.retry";

    private readonly RuntimeBootstrapGate _gate;
    private readonly Action<string>? _log;

    /// <summary>创建路由。</summary>
    /// <param name="gate">重试闸门（Signal 放行引导循环）。</param>
    /// <param name="log">日志回调（可选）。</param>
    public BootstrapCommandRouter(RuntimeBootstrapGate gate, Action<string>? log = null)
    {
        _gate = gate;
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

        _log?.Invoke("[bootstrap] 进度页请求重试");
        _gate.Signal();
        return ValueTask.FromResult("{}");
    }
}
