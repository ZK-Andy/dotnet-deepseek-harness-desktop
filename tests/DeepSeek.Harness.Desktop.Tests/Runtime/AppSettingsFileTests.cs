using System.Text.Json;
using DeepSeek.Harness.Desktop.Infrastructure.Runtime;

namespace DeepSeek.Harness.Desktop.Tests.Runtime;

/// <summary>随包 appsettings.json 的可解析性门禁（ADR frameless-uniform-caption-bar 的回归钉）：
/// 该文件由 Ryn 应用构建器直接装载（<c>RynApplicationBuilder.Build</c> → Microsoft.Extensions.
/// Configuration），**JSON 非法即启动崩**——而各选项类型的 <c>Load</c> 是 fail-safe（坏 JSON 回退默认）、
/// 单测只喂自造 JSON 串，故文件本身被清空/写坏时单元测试全绿、发货后五腿安装冒烟全红
/// （v0.6.3 实证：appsettings.json 被误写为 0 字节 → 五腿冒烟全红）。本测试读**仓库源文件**
/// （测试输出目录的副本会被其它测试以假值覆写，不可作证），把「文件可解析且关键取值在位」机器化。</summary>
public class AppSettingsFileTests
{
    /// <summary>验证源 appsettings.json 是合法 JSON 对象，且关键节的关键取值在位
    /// （断言文件独有的显式值，非默认值——默认值断言是循环证明）。</summary>
    [Fact]
    public void SourceFile_IsValidJsonWithExpectedSections()
    {
        string path = Path.Combine(
            TestRepoRoot.Find(), "src", "DeepSeek.Harness.Desktop", "appsettings.json");
        Assert.True(File.Exists(path), $"appsettings.json 缺失：{path}");
        string text = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal(
            "ZK-Andy/dotnet-deepseek-harness-desktop",
            doc.RootElement.GetProperty("Update").GetProperty("Repository").GetString());
        Assert.Equal(
            "@deepseek-ai/dsh@next",
            doc.RootElement.GetProperty("RuntimeBootstrap").GetProperty("DshSpec").GetString());
        Assert.True(
            doc.RootElement.GetProperty("RuntimeTimeouts").GetProperty("SpawnTimeoutSeconds").GetInt32() > 0,
            "RuntimeTimeouts 节被掏空");
        // 节名与解析器一致（CaptionBarOptions 的节名是 CaptionBar，非类型名）；高度 52 在本测试中只作解析通路凭证
        Assert.Equal(52, CaptionBarOptions.Parse(text).HeightPx);
    }
}
