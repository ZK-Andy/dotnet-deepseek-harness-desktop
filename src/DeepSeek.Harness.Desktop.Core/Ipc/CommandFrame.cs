using System.Text;
using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Core.Ipc;

/// <summary>
/// 命令参数帧读取单点（跨包契约层，与 <see cref="HostCommandIds"/> 同族）：页面→宿主的命令
/// 参数帧（<c>{"enabled":true}</c> 这类开关帧）的取值规则在此定案——壳侧各路由不再各写一份
/// 同形的 JSON 取值逻辑，改帧形状只改这里一处。
/// </summary>
public static class CommandFrame
{
    /// <summary>开关型参数帧的字段名。</summary>
    private const string EnabledField = "enabled";

    /// <summary>读开关型参数帧的 <c>enabled</c> 字段。</summary>
    /// <param name="args">命令参数帧（UTF-8 JSON 字节）。</param>
    /// <returns>字段存在且为 JSON <c>true</c> 时为 true；字段缺失、值为其它类型或为 <c>false</c> 一律按 false。</returns>
    /// <remarks>帧非法（不是 JSON）时抛 <see cref="JsonException"/>——调用方按既有错误帧契约处置，
    /// 不在此处吞成 false（吞掉即把「帧损坏」伪装成「用户关了开关」）。</remarks>
    public static bool ReadEnabled(ReadOnlyMemory<byte> args)
    {
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(args.Span));
        return doc.RootElement.TryGetProperty(EnabledField, out JsonElement e) &&
               e.ValueKind == JsonValueKind.True;
    }
}
