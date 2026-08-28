using System.Text.Json;
using System.Text.Json.Serialization;
using DeepSeek.Harness.Desktop.Services.Tray;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>命令路由统一错误帧：<c>{"error":"..."}</c>，失败路径不抛 IPC 异常、由页面按 error 展示。</summary>
/// <param name="Error">人读失败原因（异常消息经默认编码器转义）。</param>
internal sealed record ErrorFrame(string Error);

/// <summary>外部链接打开失败帧：宿主推给页面（companion 渲染 toast，R2 N2）。</summary>
/// <param name="Url">未能打开的站外 URL（用户可据此手动复制）。</param>
internal sealed record ExternalLinkOpenerFailedFrame(string Url);

/// <summary>
/// 宿主帧/持久化文档的 JSON 序列化通道：源生成上下文（AOT 兼容裕度）。发布产物为 JIT
/// （csproj <c>PublishAot=false</c>），源生成保留使 AOT
/// 随时可开——新增帧一律定义 internal record 并在此加一行
/// <c>[JsonSerializable]</c> 注册，漏注册编译期即失败。键名 = 属性名经 CamelCase 策略推导；
/// 改属性名即改线协议，须对照 companion 消费侧。
/// 范围例外：profile 清单等常量字段模板（DesktopProfileBootstrap 手拼，缩进排版须与上游
/// initProfile 逐字对齐）与 JsonNode DOM 合并面（MarketInstallHelper）不经本通道；
/// 解析方向一律 <c>JsonDocument</c>（桥接回包有再序列化怪癖）。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ErrorFrame))]
[JsonSerializable(typeof(AutostartCommandRouter.StateFrame), TypeInfoPropertyName = "AutostartState")]
[JsonSerializable(typeof(CloseToTrayCommandRouter.StateFrame), TypeInfoPropertyName = "CloseToTrayState")]
[JsonSerializable(typeof(CloseBehaviorPreference.PreferencesFile))]
[JsonSerializable(typeof(RunMarker.MarkerFile))]
[JsonSerializable(typeof(DesktopDiagnosticsCommandRouter.PathFrame))]
[JsonSerializable(typeof(Update.UpdateStateFrame))]
[JsonSerializable(typeof(Update.UpdateStateMachine.ReadyRecord))]
[JsonSerializable(typeof(RecoveryPageBuilder.Payload))]
[JsonSerializable(typeof(ExternalLinkOpenerFailedFrame))]
internal partial class AppJsonContext : JsonSerializerContext
{
    /// <summary>JS 字符串字面量（默认编码器全量转义，<c>&lt;</c> 与非 ASCII 均 \u 形态）：
    /// 横幅与恢复页脚本嵌值共用。</summary>
    internal static string JsString(string value)
    {
        // null 经序列化会输出裸 null 字面量，在 JS 字面量位置是静默陷阱——fail loud
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, Default.String);
    }

    /// <summary>序列化统一错误帧。</summary>
    internal static string Error(string message) =>
        JsonSerializer.Serialize(new ErrorFrame(message), Default.ErrorFrame);
}
