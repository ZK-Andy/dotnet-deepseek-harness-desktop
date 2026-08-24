using System.Text;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services.Tray;

/// <summary>
/// 宿主命令路由：<c>desktop.tray.event</c>——companion 把 Ryn 托盘插件的 Web 层事件中继回宿主，
/// 在此解析为原生动作（显示主窗 / 检查更新 / 退出）。托盘点击的原生语义只能走这条公开链路：
/// <c>TrayService.EmitEvent</c> 是插件内部属性，NativeAOT 下反射私有面不可用（ADR shell-tray-hide-to-tray）。
/// </summary>
/// <remarks>
/// 返回帧约定与 <c>app.openExternal</c> 一致："{}" = 动作已受理；"null" = 事件被忽略
/// （非托盘事件 / 未知条目 / 坏载荷——Web 层可能出现任意未来事件名，非错误）。
/// 窗口动作经委托注入而非直接依赖 IRynWindow：退出路径的契约是「先批准关窗闸门再 Close」
/// 的**顺序**，测试需要记序 fake（对齐 ExternalLinkCommandRouter 的 opener 注入先例）。
/// </remarks>
public sealed class DesktopTrayCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名（companion 中继 <c>__ryn.invoke('desktop.tray.event', {{ event, data }})</c> 触发）。</summary>
    public const string CommandName = "desktop.tray.event";

    private readonly Func<Task> _showWindow;
    private readonly Action _closeWindow;
    private readonly CloseGate _closeGate;
    private readonly Update.UpdateStateMachine? _updateMachine;
    private readonly Action<string>? _log;
    private readonly Action<string, string>? _notify;

    /// <summary>创建路由。</summary>
    /// <param name="showWindow">显示主窗动作（宿主接线为 deferred 窗口的 ShowAsync）。</param>
    /// <param name="closeWindow">关闭窗口动作（宿主接线为 deferred 窗口的 Close）。</param>
    /// <param name="closeGate">关窗闸门：退出路径先批准再 Close，放行 hide-to-tray 拦截。</param>
    /// <param name="updateMachine">自更新状态机；未装载（dev 门禁）时「检查更新」无动作。</param>
    /// <param name="log">日志回调（可选）。</param>
    /// <param name="notify">托盘通知回调（可选）：菜单触发的检查没有页面反馈面，结论经系统
    /// 托盘通知送达（标题, 正文）。设置页手动检查不走这里，避免双重打扰。</param>
    public DesktopTrayCommandRouter(
        Func<Task> showWindow,
        Action closeWindow,
        CloseGate closeGate,
        Update.UpdateStateMachine? updateMachine,
        Action<string>? log = null,
        Action<string, string>? notify = null)
    {
        _showWindow = showWindow;
        _closeWindow = closeWindow;
        _closeGate = closeGate;
        _updateMachine = updateMachine;
        _log = log;
        _notify = notify;
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

        (string? Event, string? Data) payload = (null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(args.Span));
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("event", out var ev) && ev.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    payload.Event = ev.GetString();
                }

                if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    payload.Data = d.GetString();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 坏载荷按未知事件处理：解析为 null 后走忽略分支
        }

        var action = TrayMenuActions.TryResolve(payload.Event, payload.Data);
        switch (action)
        {
            case TrayAction.ShowMainWindow:
                _ = ShowWindowSafeAsync();
                break;
            case TrayAction.CheckUpdate:
                if (_updateMachine is { } machine)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await machine.CheckAsync(cancellationToken);
                            var message = TrayCheckFeedback.Message(result);
                            if (message is not null)
                            {
                                _notify?.Invoke(TrayCheckFeedback.Title, message);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            _log?.Invoke($"[tray] 更新检查失败：{ex.Message}");
                            _notify?.Invoke(TrayCheckFeedback.Title, "检查更新失败：" + ex.Message);
                        }
                    });
                }

                break;
            case TrayAction.Quit:
                // 先批准再关窗：hide-to-tray 拦截唯一放行通道。顺序即契约（有回归测试钉住）
                _closeGate.ApproveExit();
                try
                {
                    _closeWindow();
                    _log?.Invoke("[tray] 托盘菜单退出：已放行关窗");
                }
                catch (Exception ex)
                {
                    // 窗口已销毁等极端路径：进程随宿主 Run 结束自然退出，此处留证即可
                    _log?.Invoke($"[tray] 退出关窗失败：{ex.Message}");
                }

                break;
            default:
                // 非托盘事件 / 未知条目 / 坏载荷：忽略。Web 层可能出现任意未来事件名，非错误。
                return ValueTask.FromResult("null");
        }

        return ValueTask.FromResult("{}");

        async Task ShowWindowSafeAsync()
        {
            try
            {
                await _showWindow();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[tray] 显示主窗失败：{ex.Message}");
            }
        }
    }
}
