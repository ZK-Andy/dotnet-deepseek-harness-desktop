using DeepSeek.Harness.Desktop.Core.Ipc;

namespace DeepSeek.Harness.Desktop.Tests.Fakes;

/// <summary>命令路由假体（ADR avalonia-g2-shell-bridge 的分发面测试用）：按固定命令名应答，
/// 可返回任意 JSON 或抛异常，并记录收到的参数帧原文。</summary>
internal sealed class FakeCommandRouter : ICommandRouter
{
    private readonly string _command;
    private readonly Func<string, string>? _respond;
    private readonly Exception? _failure;
    private readonly List<string> _receivedArgs = new();

    /// <summary>创建假路由。</summary>
    /// <param name="command">命令 ID。</param>
    /// <param name="respond">应答工厂（入参为参数帧原文）。</param>
    /// <param name="failure">路由时抛出的异常（可选）。</param>
    public FakeCommandRouter(string command, Func<string, string>? respond = null, Exception? failure = null)
    {
        _command = command;
        _respond = respond;
        _failure = failure;
    }

    /// <summary>收到的参数帧原文（按调用序）。</summary>
    public IReadOnlyList<string> ReceivedArgs
    {
        get
        {
            lock (_receivedArgs)
            {
                return _receivedArgs.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool CanRoute(string command) => string.Equals(command, _command, StringComparison.Ordinal);

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(
        string command,
        ReadOnlyMemory<byte> args,
        CancellationToken cancellationToken)
    {
        if (!CanRoute(command))
        {
            throw new CommandNotFoundException(command);
        }

        string body = System.Text.Encoding.UTF8.GetString(args.Span);
        lock (_receivedArgs)
        {
            _receivedArgs.Add(body);
        }

        if (_failure is not null)
        {
            throw _failure;
        }

        return ValueTask.FromResult(_respond?.Invoke(body) ?? "null");
    }
}
