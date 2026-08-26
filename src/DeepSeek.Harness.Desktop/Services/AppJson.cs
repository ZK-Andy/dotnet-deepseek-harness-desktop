using System.Text.Json;
using System.Text.Json.Serialization;
using DeepSeek.Harness.Desktop.Services.Tray;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>命令路由统一错误帧：<c>{"error":"..."}</c>，失败路径不抛 IPC 异常、由页面按 error 展示。</summary>
/// <param name="Error">人读失败原因（异常消息经默认编码器转义）。</param>
internal sealed record ErrorFrame(string Error);

/// <summary>
/// 宿主唯一的 JSON 序列化通道：源生成上下文（NativeAOT 安全）。反射式序列化在
/// <c>PublishAot</c> 下不可用（IL2026/IL3050），手拼字符串已由本通道取代——新增帧一律
/// 定义 internal record 并在此加一行 <c>[JsonSerializable]</c> 注册，漏注册编译期即失败。
/// 键名 = 属性名经 CamelCase 策略推导；改属性名即改线协议，须对照 companion 消费侧。
/// 解析方向不走本上下文（桥接回包有再序列化怪癖），一律 <c>JsonDocument</c>。
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
internal partial class AppJsonContext : JsonSerializerContext
{
    /// <summary>JS 字符串字面量（默认编码器全量转义，<c>&lt;</c> 与非 ASCII 均 \u 形态）：
    /// 横幅与恢复页脚本嵌值共用；<paramref name="value"/> 不得为 null。</summary>
    internal static string JsString(string value) => JsonSerializer.Serialize(value, Default.String);

    /// <summary>序列化统一错误帧。</summary>
    internal static string Error(string message) =>
        JsonSerializer.Serialize(new ErrorFrame(message), Default.ErrorFrame);
}
