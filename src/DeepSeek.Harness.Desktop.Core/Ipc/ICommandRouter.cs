namespace DeepSeek.Harness.Desktop.Core.Ipc;

/// <summary>
/// 页面命令路由契约（R3/IPC 强类型）：一条路由声明自己认领哪些命令 ID，并在被分发时返回
/// 一个 JSON 回包。命令 ID 的单一事实源是 <see cref="HostCommandIds"/>；未注册的命令由分发点
/// 直接回错误帧（deny-by-default，见 ADR host-command-ids）。
/// </summary>
/// <remarks>
/// 分发点（<c>ShellBridge</c>）按路由表顺序取第一个 <see cref="CanRoute"/> 为真的路由，
/// 因此 <see cref="CanRoute"/> 必须是纯判定：不得有副作用、不得依赖调用时序。
/// 回包是 JSON 文本（合法 JSON 表达式），错误帧同样走回包表达——异常只用于「路由与声明不符」
/// 这类装配错误，分发点会把异常转成错误帧并留痕（fail loud，但不炸页面）。
/// </remarks>
public interface ICommandRouter
{
    /// <summary>本路由是否认领该命令。</summary>
    /// <param name="command">页面→宿主的命令 ID（如 <c>desktop.update.check</c>）。</param>
    /// <returns>认领时为 true。</returns>
    bool CanRoute(string command);

    /// <summary>处理一条命令并返回 JSON 回包。</summary>
    /// <param name="command">命令 ID（调用方保证 <see cref="CanRoute"/> 已为 true）。</param>
    /// <param name="args">参数帧（UTF-8 JSON 字节；无参数时为空）。</param>
    /// <param name="cancellationToken">取消令牌（页面销毁/桥收口时触发）。</param>
    /// <returns>JSON 回包文本。</returns>
    /// <exception cref="CommandNotFoundException">命令与本路由的声明不符（装配漂移）。</exception>
    ValueTask<string> RouteAsync(
        string command,
        ReadOnlyMemory<byte> args,
        CancellationToken cancellationToken);
}
