using System.Text;
using System.Text.Json;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主命令路由：<c>desktop.preinstall.choose</c>——wwwroot 引导页「插件引导」步的
/// 「确认安装/跳过」按钮经此放行决策（ADR reference-alignment 批次二）。
/// 载荷 <c>{"action":"install"|"skip"}</c>；非 "skip" 一律按安装处理（推荐动作，包容未知载荷）。
/// </summary>
public sealed class PreinstallCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名。</summary>
    public const string CommandName = "desktop.preinstall.choose";

    private readonly PreinstallChoiceGate _gate;
    private readonly Action<string>? _log;

    /// <summary>创建路由。</summary>
    /// <param name="gate">插件引导决策闸门（置位用户选择）。</param>
    /// <param name="log">日志回调（可选）。</param>
    public PreinstallCommandRouter(PreinstallChoiceGate gate, Action<string>? log = null)
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

        PreinstallChoice choice = ParseChoice(args);
        _log?.Invoke($"[host] 插件引导：用户{(choice == PreinstallChoice.Install ? "确认安装" : "跳过")}");
        _gate.Set(choice);
        return ValueTask.FromResult("{}");
    }

    /// <summary>解析决策载荷：<c>{"action":"skip"}</c> 判跳过，其余按安装。</summary>
    private static PreinstallChoice ParseChoice(ReadOnlyMemory<byte> args)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(args.Span));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("action", out JsonElement a) &&
                a.ValueKind == JsonValueKind.String &&
                string.Equals(a.GetString(), "skip", StringComparison.Ordinal))
            {
                return PreinstallChoice.Skip;
            }
        }
        catch (JsonException)
        {
            // 坏载荷按推荐动作（安装）处理；invoke 方自带 catch，不影响引导链路
        }

        return PreinstallChoice.Install;
    }
}
