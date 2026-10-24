using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>
/// 页面健康探针契约（ADR relay-restart-client-module-collapse + architecture/2026-10-21-window-script-target-port）：
/// 探针回报可见文本长度观测值，<see cref="PageHealthMonitor.Parse"/> 是「DOM 形态 → 健康态」的单一判读点；
/// 壳自有文档（wwwroot 引导页、崩溃恢复页）在内容判据下必须仍判 Alive，否则新判据会把壳页面当空白反复 reload。
/// 探针路经脚本求值端口（求值与解壳各只有一处），故回报形态是<b>已解码</b>的观测值。
/// </summary>
public class PageHealthMonitorTests
{
    /// <summary>DOM 形态观测值 → 健康态表判：可见文本长度 0 判 Dead、非零判 Alive，形状读不懂
    /// （含旧观测值、带符号/空白/非十进制的乱值）判 Unknown（不计入探针计数，绝不因读不懂触发恢复）。
    /// 入参是已解码观测值：带引号的形态（解壳前）判 Unknown——解壳只有 <c>ScriptProbeValue.Decode</c> 一处。</summary>
    [Theory]
    [InlineData("text:0", PageHealth.Dead)]
    [InlineData("text:1", PageHealth.Alive)]
    [InlineData("text:42", PageHealth.Alive)]
    [InlineData("\"text:42\"", PageHealth.Unknown)]
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
    public void Parse_TablesDomShapeToHealth(string? decoded, PageHealth expected)
    {
        Assert.Equal(expected, PageHealthMonitor.Parse(decoded));
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

    /// <summary>探针路端到端：桥回报的 JSON 字符串外壳解壳后判 Alive，快照落 alive——
    /// 「求值经端口、解壳只有一处」在真实回包形态（JSON 字符串字面量）下的判读结论。</summary>
    [Fact]
    public async Task RunAsync_DecodedProbe_RecordsAlive()
    {
        var target = new FakeWindowScriptTarget();
        target.EnqueueResult("\"text:42\"");
        var monitor = new PageHealthMonitor(target);
        using var cts = new CancellationTokenSource();

        Task run = monitor.RunAsync(TimeSpan.FromMilliseconds(1), cts.Token);
        await WaitUntilAsync(() => monitor.Snapshot is not null);
        await cts.CancelAsync();
        await run;

        Assert.Contains("alive", monitor.Snapshot!, StringComparison.Ordinal);
        Assert.True(target.Scripts.Count >= 1);
    }

    /// <summary>探针抛异常（窗口未就绪等）：记 Unknown、不外抛、续跑——观测面绝不拖垮主流程。
    /// Unknown 不计入探针计数，故快照仍为 null（尚无有效观测值）。</summary>
    [Fact]
    public async Task RunAsync_ProbeThrows_RecordsUnknownAndKeepsRunning()
    {
        var target = new FakeWindowScriptTarget();
        target.Enqueue(new InvalidOperationException("窗口尚未就绪"));
        var monitor = new PageHealthMonitor(target);
        using var cts = new CancellationTokenSource();

        Task run = monitor.RunAsync(TimeSpan.FromMilliseconds(1), cts.Token);
        await WaitUntilAsync(() => target.Scripts.Count >= 2);
        await cts.CancelAsync();
        await run;

        Assert.Null(monitor.Snapshot);
    }

    /// <summary>取消令牌透传到求值端口（钉子：端口的 ct 形参有真实消费方，不是装饰）。</summary>
    [Fact]
    public async Task RunAsync_PropagatesCancellationTokenToScriptTarget()
    {
        var target = new FakeWindowScriptTarget();
        var monitor = new PageHealthMonitor(target);
        using var cts = new CancellationTokenSource();

        Task run = monitor.RunAsync(TimeSpan.FromMilliseconds(1), cts.Token);
        await WaitUntilAsync(() => target.Scripts.Count >= 1);
        await cts.CancelAsync();
        await run;

        Assert.Equal(cts.Token, target.LastCancellationToken);
    }

    /// <summary>有界轮询等待（监视器是循环体，无返回句柄）：到点未满足即断言失败。</summary>
    /// <param name="condition">等待条件。</param>
    /// <param name="timeoutMilliseconds">上限（毫秒）。</param>
    /// <returns>条件满足即完成的任务。</returns>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        for (int waited = 0; waited < timeoutMilliseconds; waited += 10)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "等待探针条件超时");
    }
}
