using System.Text.Json.Serialization;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// Core 侧帧的 JSON 源生成上下文（AOT 兼容裕度）：B1 起 <see cref="UpdateStateFrame"/> 随
/// <see cref="UpdateState"/> 迁入 Core，序列化通道随类型走——选项（CamelCase + Serialization 模式）
/// 与主工程 AppJsonContext 保持一致；该帧的键序/缺省语义是 companion 消费契约，改动须两侧同审。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(UpdateStateFrame))]
internal partial class UpdateJsonContext : JsonSerializerContext;
