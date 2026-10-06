using System.Text.Json;
using DeepSeek.Harness.Desktop.Infrastructure.Runtime;

namespace DeepSeek.Harness.Desktop.Tests.Runtime;

/// <summary>随包 appsettings.json 的可解析性门禁（ADR frameless-uniform-caption-bar 的回归钉）：
/// 该文件由 Ryn 应用构建器直接装载（<c>RynApplicationBuilder.Build</c> → Microsoft.Extensions.
/// Configuration），**JSON 非法即启动崩**——而各选项类型的 <c>Load</c> 是 fail-safe（坏 JSON 回退默认）、
/// 单测只喂自造 JSON 串，故文件本身被清空/写坏时单元测试全绿、发货后五腿安装冒烟全红
/// （v0.6.3 实证：appsettings.json 被误写为 0 字节 → 五腿冒烟全红）。本测试读**随包副本**
/// （AppContext.BaseDirectory，与 wwwroot 占位页测试同款），把「文件可解析且关键节生效」钉成机器门禁。</summary>
public class AppSettingsFileTests
{
    /// <summary>验证随包 appsettings.json 是合法 JSON 对象，且关键节在位并能被各自的 Parse 消费
    /// （CaptionBar 高度 = 文件里的显式值，即文件内容真的生效而非回退默认）。</summary>
    [Fact]
    public void ShippedFile_IsValidJsonWithExpectedSections()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"appsettings.json 缺失：{path}");
        string text = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        Assert.Equal(52, CaptionBarOptions.Parse(text).HeightPx);
        Assert.True(doc.RootElement.TryGetProperty("RuntimeTimeouts", out _), "RuntimeTimeouts 节缺失");
        Assert.True(doc.RootElement.TryGetProperty("Update", out _), "Update 节缺失");
    }
}
