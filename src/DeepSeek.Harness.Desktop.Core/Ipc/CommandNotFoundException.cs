namespace DeepSeek.Harness.Desktop.Core.Ipc;

/// <summary>
/// 命令未找到（装配漂移信号）：路由的 <see cref="ICommandRouter.CanRoute"/> 声明认领某命令，
/// 但 <see cref="ICommandRouter.RouteAsync"/> 的 switch 里没有对应分支。分发点把它转成错误帧；
/// 正常路径上不应出现——出现即说明路由表的声明与实现已不同步。
/// </summary>
public sealed class CommandNotFoundException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="command">未被认领的命令 ID。</param>
    public CommandNotFoundException(string command)
        : base($"未注册的命令：{command}")
    {
        Command = command;
    }

    /// <summary>未被认领的命令 ID。</summary>
    public string? Command { get; }
}
