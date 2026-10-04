using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 宿主命令路由：<c>desktop.app.restart</c>——companion「桌面设置」重启行的宿主语义。
/// 受理即回 <c>{}</c>，实际重启延迟 <see cref="ResponseFlushDelay"/> 执行：给 IPC 响应留冲刷窗口，
/// 页面不至于把「已受理」等成 4s 超时。执行序先 <see cref="CloseGate.ApproveExit"/> 再重启
/// （与托盘退出同一「先批准再关窗」契约，hide-to-tray 拦截不放行即转成隐藏）。
/// </summary>
/// <remarks>
/// 通道失效场景本命令同样发不出（页面→壳 invoke 整体不可达），该场景由托盘菜单兜底——
/// 托盘是原生事件链路，不依赖 Web 通道（ADR app-restart-native-switch）。
/// </remarks>
public sealed class AppRestartCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名。</summary>
    public const string CommandName = "desktop.app.restart";

    /// <summary>受理帧返回后的重启延迟：协议常量非可调参数——只服务「响应冲刷」这一个目的，
    /// 量级由 IPC 往返决定，不随部署环境变化。</summary>
    internal static readonly TimeSpan ResponseFlushDelay = TimeSpan.FromMilliseconds(200);

    private readonly CloseGate _closeGate;
    private readonly Action _restart;
    private readonly Action<string>? _log;
    private readonly TimeSpan _flushDelay;

    /// <summary>创建路由；日志委托默认不接（生产传 HostLog.Write，测试注入收集器）。</summary>
    /// <param name="closeGate">关窗闸门：重启前先批准，放行 hide-to-tray 拦截。</param>
    /// <param name="restart">重启动作（宿主接线为 <c>ExitPipeline.Restart</c> + 拉起新实例）。</param>
    /// <param name="log">日志回调（可选）。</param>
    /// <param name="flushDelay">受理→执行延迟（测试注入 0 免等待；缺省用协议常量）。</param>
    public AppRestartCommandRouter(
        CloseGate closeGate,
        Action restart,
        Action<string>? log = null,
        TimeSpan? flushDelay = null)
    {
        _closeGate = closeGate;
        _restart = restart;
        _log = log;
        _flushDelay = flushDelay ?? ResponseFlushDelay;
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

        _log?.Invoke($"[host] 重启应用：已受理（{_flushDelay.TotalMilliseconds:0}ms 后执行）");
        // 受理即承诺：延迟段不接请求级取消令牌（Ryn 0.38.0 生产路径传 None；若未来传真实
        // per-request token，响应后取消会把已承诺的重启静默掐掉——评审 R2 采纳项）
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_flushDelay);
                _closeGate.ApproveExit();
                _restart();
            }
            catch (Exception ex)
            {
                // 重启动作本身失败（拉起新实例失败/闸门外异常）：留痕即处置——此时壳仍在运行，
                // 用户可重试或改走托盘菜单；吞掉不记会把失败变成「点了没反应」盲区（D003：命名所吞）
                _log?.Invoke($"[host] 重启应用执行失败：{ex.Message}");
            }
        });
        return ValueTask.FromResult("{}");
    }
}
