using System.Text;
using System.Text.Json;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 宿主命令路由：<c>desktop.companion.report</c>——伴生插件把页面侧的降级事实上报进 host.log
/// （ADR companion-switch-degradation-recovery）。设置页开关行的失败只活在页面内存里时，事后
/// 复盘在 host.log 上零证据（2026-10-01 实机：通道健康、开关不动、日志空白）；故开此单向报障口：
/// 页面侧的失败入队即先发一次，失败/悬挂回队，待本页下一次经 <c>invokeWithTimeout</c> 的成功调用
/// （开关重试 / 更新重试 / 探针）补投，失败现场首次即可复盘。
/// </summary>
/// <remarks>
/// 只写诊断日志：不承载状态、不参与任何主链路，坏载荷/空载荷静默忽略（返回 <c>null</c>），
/// 绝不抛 IPC 异常。自由文本单行化 + 截断——日志注入与刷屏都从此处挡住。
/// </remarks>
public sealed class CompanionReportCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名。</summary>
    public const string CommandName = "desktop.companion.report";

    /// <summary>单条上报消息的字符上限（超出截断并留省略号）。</summary>
    internal const int MaxMessageLength = 400;

    private readonly Action<string>? _log;

    /// <summary>创建路由；日志委托默认不接（生产传 HostLog.Write，测试注入收集器）。</summary>
    public CompanionReportCommandRouter(Action<string>? log = null) => _log = log;

    /// <inheritdoc />
    public bool CanRoute(string command) => string.Equals(command, CommandName, StringComparison.Ordinal);

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!CanRoute(command))
        {
            throw new RynCommandNotFoundException(command);
        }

        string? scope = null;
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(args.Span));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                scope = ReadString(doc.RootElement, "scope");
                message = ReadString(doc.RootElement, "message");
            }
        }
        catch (JsonException)
        {
            // 坏载荷按未上报处理：报障是增强能力，坏包不得制造第二个失败面
        }

        if (string.IsNullOrWhiteSpace(scope) && string.IsNullOrWhiteSpace(message))
        {
            return ValueTask.FromResult("null");
        }

        _log?.Invoke($"[companion] 页面降级上报：{Sanitize(scope)}：{Sanitize(message)}");
        return ValueTask.FromResult("{}");
    }

    /// <summary>读字符串字段；非字符串/缺失一律 null。</summary>
    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>单行化 + 截断；null/空渲染成占位符（日志行始终可读，不产出「上报：（空）」式空行）。</summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(空)";
        }

        // 折叠全部行分隔符（\r\n \v\f 与 U+0085/U+2028/U+2029）：host.log 逐条 append，
        // 一条上报必须恰好落成一行，否则读取器会把伪造换行当成独立日志行。
        string flat = value
            .Replace('\r', ' ').Replace('\n', ' ').Replace('\v', ' ').Replace('\f', ' ')
            .Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ')
            .Trim();
        return flat.Length <= MaxMessageLength ? flat : flat[..MaxMessageLength] + "…";
    }
}
