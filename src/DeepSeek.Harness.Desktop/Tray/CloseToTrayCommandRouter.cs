using System.Text.Json;
using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Tray;

/// <summary>
/// 宿主命令路由：<c>desktop.closeToTray.getState / set</c>。companion「关闭时最小化到托盘」
/// 开关经此读写；set 帧形如 <c>{"enabled":true}</c>。状态帧携带 <c>available</c>
/// （系统托盘是否就绪）：无托盘环境隐藏无从谈起，客户端据此禁用开关。
/// </summary>
public sealed class CloseToTrayCommandRouter : ICommandRouter
{
    private readonly ICloseBehaviorPreference _preference;
    private readonly Func<bool> _available;
    private readonly Action<string>? _log;

    /// <summary>创建路由。<paramref name="available"/> 惰性求值——注册早于托盘初始化。</summary>
    public CloseToTrayCommandRouter(ICloseBehaviorPreference preference, Func<bool> available, Action<string>? log = null)
    {
        _preference = preference;
        _available = available;
        _log = log;
    }

    /// <inheritdoc />
    public bool CanRoute(string command) =>
        command is HostCommandIds.CloseToTrayGetState or HostCommandIds.CloseToTraySet;

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args, CancellationToken cancellationToken)
    {
        if (!CanRoute(command))
        {
            throw new CommandNotFoundException(command);
        }

        try
        {
            switch (command)
            {
                case HostCommandIds.CloseToTrayGetState:
                    return ValueTask.FromResult(Frame());
                case HostCommandIds.CloseToTraySet:
                    bool enabled = CommandFrame.ReadEnabled(args);
                    _preference.Set(enabled);
                    _log?.Invoke($"[host] 关闭最小化到托盘已{(enabled ? "开启" : "关闭")}");
                    return ValueTask.FromResult(Frame());
                default:
                    throw new CommandNotFoundException(command);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[host] 关闭托盘偏好写入失败：{ex.Message}");
            return ValueTask.FromResult(AppJsonContext.Error(ex.Message));
        }
    }

    /// <summary>状态帧；internal 供 <see cref="AppJsonContext"/> 源生成注册。键序 = 声明序
    /// （<c>enabled</c> 在前，CloseToTrayTests 精确串断言）。</summary>
    /// <param name="Enabled">关闭按钮是否隐藏到托盘。</param>
    /// <param name="Available">系统托盘是否就绪（无托盘环境客户端据此禁用开关）。</param>
    internal sealed record StateFrame(bool Enabled, bool Available);

    private string Frame() =>
        JsonSerializer.Serialize(new StateFrame(_preference.HideOnClose, _available()), AppJsonContext.Default.CloseToTrayState);
}
