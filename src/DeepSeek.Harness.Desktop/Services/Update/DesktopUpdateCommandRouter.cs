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
    private readonly TextWriter? _log;

    /// <summary>创建路由。</summary>
    /// <param name="machine">自更新状态机单例。</param>
    /// <param name="log">日志输出（可选）。</param>
    public DesktopUpdateCommandRouter(UpdateStateMachine machine, TextWriter? log = null)
    {
        _machine = machine;
        _log = log;
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
                // 检查可能耗时（下载分钟级），立即回当前态，后续靠事件推送
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _machine.CheckAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log?.WriteLine($"[update] check 失败：{ex.Message}");
                    }
                }, cancellationToken);
                return ValueTask.FromResult(_machine.State.ToJson());
            case "desktop.update.install":
                return RouteInstallAsync(cancellationToken);
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
            _log?.WriteLine($"[update] install 拒绝：{ex.Message}");
            return AppJsonContext.Error(ex.Message);
        }
    }
}
