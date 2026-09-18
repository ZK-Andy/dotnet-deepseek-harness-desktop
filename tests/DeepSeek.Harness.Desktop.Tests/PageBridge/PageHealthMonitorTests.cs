namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>
/// 页面健康探针契约（ADR relay-restart-client-module-collapse）：探针回报可见文本长度观测值，
/// <see cref="PageHealthMonitor.Parse"/> 是「DOM 形态 → 健康态」的单一判读点；壳自有文档
/// （wwwroot 引导页、崩溃恢复页）在内容判据下必须仍判 Alive，否则新判据会把壳页面当空白反复 reload。
/// </summary>
public class PageHealthMonitorTests
{
    /// <summary>DOM 形态观测值 → 健康态表判：可见文本长度 0 判 Dead、非零判 Alive，形状读不懂
    /// （含旧观测值、带符号/空白/非十进制的乱值）判 Unknown（不计入探针计数，绝不因读不懂触发恢复）。</summary>
    [Theory]
    [InlineData("text:0", PageHealth.Dead)]
    [InlineData("text:1", PageHealth.Alive)]
    [InlineData("text:42", PageHealth.Alive)]
    [InlineData("\"text:42\"", PageHealth.Alive)]
    [InlineData(" text:0 ", PageHealth.Dead)]
    [InlineData("", PageHealth.Unknown)]
    [InlineData(null, PageHealth.Unknown)]
    [InlineData("garbage", PageHealth.Unknown)]
    [InlineData("text:", PageHealth.Unknown)]
    [InlineData("text:-1", PageHealth.Unknown)]
    [InlineData("text:+5", PageHealth.Unknown)]
    [InlineData("text: 5", PageHealth.Unknown)]
    [InlineData("text:abc", PageHealth.Unknown)]
    [InlineData("alive", PageHealth.Unknown)]
    public void Parse_TablesDomShapeToHealth(string? raw, PageHealth expected)
    {
        Assert.Equal(expected, PageHealthMonitor.Parse(raw));
    }

    /// <summary>脚本与判读点同一契约：脚本只回报 <c>text:</c> 观测值（十进制非负长度），且按可见文本
    /// （innerText）下判——子节点判据正是塌陷页被误判 Alive 的旧形态。</summary>
    [Fact]
    public void ProbeScript_ReportsTextObservation_NotChildCount()
    {
        Assert.Contains("'text:'", PageHealthMonitor.ProbeScript, StringComparison.Ordinal);
        Assert.Contains("innerText", PageHealthMonitor.ProbeScript, StringComparison.Ordinal);
        Assert.DoesNotContain("childElementCount", PageHealthMonitor.ProbeScript, StringComparison.Ordinal);
    }

    /// <summary>壳自有文档在内容判据下仍判 Alive：引导页（可见 &lt;h1&gt; 与回退文案）与恢复页
    /// （骨架里作为元素文本落地的自动重试说明）都留有可见文本——夹具钉死该契约，防清空/移位文案后
    /// 探针把壳页面判 Dead 并触发有界 reload 循环。</summary>
    [Fact]
    public void ShellDocuments_HaveVisibleText_StayAlive()
    {
        string bootstrap = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
        Assert.Contains(">DeepSeek Harness Desktop</h1>", bootstrap, StringComparison.Ordinal);
        Assert.Contains("id=\"fallbackText\"", bootstrap, StringComparison.Ordinal);

        // 恢复页骨架整段经 JSON 序列化落地（非 ASCII 转义，且 < > 转义为 \u003C/\u003E），故取英文分支
        // 断言「元素文本上下文」形态：文案两侧是标签闭合边界，而非注释/属性里的同名字符串
        string recovery = RecoveryPageBuilder.BuildScript("runtime crashed", [], english: true);
        Assert.Contains(
            $"\\u003E{UiCopy.RecoveryAutoRetryNote(english: true)}\\u003C/p\\u003E",
            recovery,
            StringComparison.Ordinal);
    }
}
