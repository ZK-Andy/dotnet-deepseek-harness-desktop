using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>ryn.json 能力面契约（ADR frameless-uniform-caption-bar）：自绘顶栏的窗口控制依赖
/// Ryn 注入脚本调用 <c>window.*</c> 命令，能力沙箱必须放行该六命令——漏一项即顶栏对应功能静默失灵
/// （点击无响应），此测试把「能力面与命令面同源」钉成回归。</summary>
public class RynWindowCapabilityTests
{
    /// <summary>Ryn 注入的 titlebar 脚本实际调用的 window.* 命令全集（上游 RynWebView.cs 内嵌脚本，
    /// 以 0.38.0 为准）：三按钮 + 双端拖拽（macOS beginNativeDrag / 其他 startDrag）+ 边缘缩放。</summary>
    private static readonly string[] s_requiredWindowCommands =
    [
        "close",
        "minimize",
        "toggleMaximize",
        "startDrag",
        "startResize",
        "beginNativeDrag",
    ];

    /// <summary>验证 ryn.json 的 window 能力为对象形态且 allow 恰覆盖所需命令全集
    /// （细粒度 allow-list：不放行 setSize/setPosition/setFullscreen 等其余 window 命令）。</summary>
    [Fact]
    public void WindowCapability_AllowsTitlebarCommands()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ryn.json"));
        using var doc = JsonDocument.Parse(json);
        JsonElement window = doc.RootElement.GetProperty("capabilities").GetProperty("window");
        Assert.Equal(JsonValueKind.Object, window.ValueKind);

        string[] allow = window.GetProperty("allow").EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => s is not null)
            .Select(s => s!)
            .ToArray();

        // 恰好集相等：allow-list 的卖点是「最小化」，多出的命令同样是能力面膨胀（评审 S2）
        Assert.Equal(s_requiredWindowCommands.OrderBy(c => c).ToArray(), allow.OrderBy(c => c).ToArray());
    }
}
