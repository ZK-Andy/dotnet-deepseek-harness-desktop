using System.Text;
using System.Text.Json;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主命令路由：<c>desktop.autostart.getState / set</c>。companion「桌面」设置区块的
/// 开机自启开关经此读写；set 帧形如 <c>{"enabled":true}</c>。
/// </summary>
public sealed class AutostartCommandRouter : ICommandRouter
{
    private readonly Action<string>? _log;

    /// <summary>创建路由；日志委托默认不接（生产传 HostLog.Write，测试注入收集器）。</summary>
    public AutostartCommandRouter(Action<string>? log = null) => _log = log;

    /// <inheritdoc />
    public bool CanRoute(string command) =>
        command is "desktop.autostart.getState" or "desktop.autostart.set";

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!CanRoute(command))
        {
            throw new RynCommandNotFoundException(command);
        }

        try
        {
            switch (command)
            {
                case "desktop.autostart.getState":
                    return ValueTask.FromResult(Frame(Autostart.IsEnabled()));
                case "desktop.autostart.set":
                    var enabled = ParseEnabled(args);
                    var now = Autostart.SetEnabled(enabled);
                    _log?.Invoke($"[host] 开机自启已{(now ? "启用" : "停用")}");
                    return ValueTask.FromResult(Frame(now));
                default:
                    throw new RynCommandNotFoundException(command);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[host] 开机自启切换失败：{ex.Message}");
            return ValueTask.FromResult(AppJsonContext.Error(ex.Message));
        }
    }

    private static bool ParseEnabled(ReadOnlyMemory<byte> args)
    {
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(args.Span));
        return doc.RootElement.TryGetProperty("enabled", out var e) &&
               e.ValueKind == JsonValueKind.True;
    }

    /// <summary>状态帧 <c>{"enabled":B}</c>；internal 供 <see cref="AppJsonContext"/> 源生成注册。</summary>
    /// <param name="Enabled">开机自启是否已启用。</param>
    internal sealed record StateFrame(bool Enabled);

    private static string Frame(bool enabled) =>
        JsonSerializer.Serialize(new StateFrame(enabled), AppJsonContext.Default.AutostartState);
}
