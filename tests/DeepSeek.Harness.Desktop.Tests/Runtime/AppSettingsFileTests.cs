using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Tests.Runtime;

/// <summary>随包 appsettings.json 的可解析性门禁（ADR frameless-uniform-caption-bar 的回归钉）：
/// 该文件经 fail-safe 读取器装载（<c>ConfigSectionFile.LoadFile</c>：坏 JSON 回退默认值并留痕一行、不阻塞启动），
/// 单测又只喂自造 JSON 串——文件本身被清空/写坏时单测全绿、启动不崩而配置丢（仅装载留痕可查）
/// （v0.6.3 曾实证 0 字节文件致五腿安装冒烟全红）。本测试读**仓库源文件**
/// （测试输出目录的副本会被其它测试以假值覆写，不可作证），把「文件可解析且关键取值在位」机器化，
/// 把丢失面钉在可发现的位置。</summary>
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
        // CaptionBar 节在文件里显式在位：读原始 JSON 取值，不走 fail-safe 解析——后者缺节/坏值回退的
        // 默认恰是 52，拿它断言是循环证明（评审 S1），删掉整个节仍会绿。
        Assert.Equal(52, doc.RootElement.GetProperty("CaptionBar").GetProperty("HeightPx").GetInt32());
    }
}
