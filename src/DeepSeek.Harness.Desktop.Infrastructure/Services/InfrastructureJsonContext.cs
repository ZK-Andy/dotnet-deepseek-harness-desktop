using System.Text.Json.Serialization;
using DeepSeek.Harness.Desktop.Services.Tray;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// Infrastructure 侧持久化文档的 JSON 源生成上下文（AOT 兼容裕度）：B2 起 run-marker 落盘适配器
/// 随进程/文件边界迁入本工程，序列化通道随类型走——选项（CamelCase + Serialization 模式）
/// 与主工程 AppJsonContext 保持一致。run-marker.json 的键名/缺省语义是跨启动取证契约
/// （Release 读方按 token 键判 owner），改动须两侧同审。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(RunMarker.MarkerFile))]
[JsonSerializable(typeof(CloseBehaviorPreference.PreferencesFile))]
internal partial class InfrastructureJsonContext : JsonSerializerContext;
