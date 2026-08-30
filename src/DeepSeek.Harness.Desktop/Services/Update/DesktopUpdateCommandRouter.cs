using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// 宿主命令路由：<c>desktop.update.getState / check / install</c>。
/// 插件客户端经 <c>window.__ryn.invoke</c> 调用；状态变化另由宿主主动推 CustomEvent 进页面。
/// </summary>
public sealed class DesktopUpdateCommandRouter : ICommandRouter
{
    /// <summary>命令名前缀。</summary>
    public const string Prefix = "desktop.update.";

    private readonly UpdateStateMachine _machine;
    private readonly Action<string>? _log;
    private readonly Func<CancellationToken>? _backgroundToken;

    /// <summary>创建路由。</summary>
    /// <param name="machine">自更新状态机单例。</param>
    /// <param name="log">日志委托（可选；生产传 HostLog.Write，测试注入收集器）。</param>
    /// <param name="backgroundToken">后台长任务（check 下载 / install 兜底强退）的取消令牌来源；
    /// 未提供时按不可取消处理。生产传宿主监督器 token：check/install 是分钟级任务，不能挂
    /// IPC 请求作用域 token——请求帧返回后分发器若取消请求 token，后台任务会被连坐取消。</param>
    public DesktopUpdateCommandRouter(UpdateStateMachine machine, Action<string>? log = null, Func<CancellationToken>? backgroundToken = null)
    {
        _machine = machine;
        _log = log;
        _backgroundToken = backgroundToken;
    }

    /// <inheritdoc />
    public bool CanRoute(string command) =>
        command is "desktop.update.getState" or "desktop.update.check" or "desktop.update.install";

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!CanRoute(command))
        {
            throw new RynCommandNotFoundException(command);
        }

        switch (command)
        {
            case "desktop.update.getState":
                return ValueTask.FromResult(_machine.State.ToJson());
            case "desktop.update.check":
                {
                    // 检查可能耗时（下载分钟级），立即回当前态，后续靠事件推送
                    var backgroundCt = _backgroundToken?.Invoke() ?? CancellationToken.None;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _machine.CheckAsync(backgroundCt).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _log?.Invoke($"[update] check 失败：{ex.Message}");
                        }
                    }, backgroundCt);
                    return ValueTask.FromResult(_machine.State.ToJson());
                }
            case "desktop.update.install":
                return RouteInstallAsync(_backgroundToken?.Invoke() ?? CancellationToken.None);
            default:
                throw new RynCommandNotFoundException(command);
        }
    }

    private async ValueTask<string> RouteInstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _machine.InstallAsync(cancellationToken).ConfigureAwait(false);
            // 成功路径不返回：安装器派生后壳即将退出
            return "{}";
        }
        catch (InvalidOperationException ex)
        {
            _log?.Invoke($"[update] install 拒绝：{ex.Message}");
            return AppJsonContext.Error(ex.Message);
        }
    }
}
