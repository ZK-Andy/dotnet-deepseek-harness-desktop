namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>终页三态裁决（ADR page-verdict-gate）：同源 + 非空 + 不含鉴权标记才 healthy；
/// 鉴权标记 auth；探测失败/非同源/空文本 unknown。</summary>
public class WebAuthRecoveryTests
{
    private const string Origin = "http://127.0.0.1:40683";
    private const string Auth = "dsh web authentication required; reopen the URL printed by dsh web.";

    private static string Sample(string origin, string text) => origin + PageProbeSample.Separator + text;

    /// <summary>测试内薄包：生产入口是 <c>ClassifyDetail</c>（明细供日志层），断言点只看三态。</summary>
    private static WebAuthRecovery.PageVerdict Classify(string? raw, string expectedOrigin) =>
        WebAuthRecovery.ClassifyDetail(raw, expectedOrigin).Verdict;

    /// <summary>探针失败（null）→ 未知：观测失败绝不判健康。</summary>
    [Fact]
    public void NullSample_IsUnknown()
    {
        Assert.Equal(WebAuthRecovery.PageVerdict.Unknown, Classify(null, Origin));
    }

    /// <summary>缺分隔符（探针脚本与拆分协议漂移）→ 未知，不猜。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("http://127.0.0.1:40683")]
    public void MalformedSample_IsUnknown(string raw)
    {
        Assert.Equal(WebAuthRecovery.PageVerdict.Unknown, Classify(raw, Origin));
    }

    /// <summary>非同源（占位页 ryn://app、引导页）→ 未知，不算进了 dsh UI。</summary>
    [Theory]
    [InlineData("ryn://app")]
    [InlineData("http://127.0.0.1:1")]
    [InlineData("http://localhost:40683")]
    public void ForeignOrigin_IsUnknown(string origin)
    {
        Assert.Equal(WebAuthRecovery.PageVerdict.Unknown, Classify(Sample(origin, "DeepSeek Harness"), Origin));
    }

    /// <summary>同源 + 正常文本 → 健康（origin 大小写不敏感）。</summary>
    [Theory]
    [InlineData("DeepSeek Harness")]
    [InlineData("新会话")]
    public void SameOriginText_IsHealthy(string text)
    {
        Assert.Equal(WebAuthRecovery.PageVerdict.Healthy, Classify(Sample(Origin, text), Origin));
        Assert.Equal(WebAuthRecovery.PageVerdict.Healthy, Classify(Sample(Origin.ToUpperInvariant(), text), Origin));
    }

    /// <summary>文本内含分隔符同串 → 只按首个切分，仍判健康（不误伤）。</summary>
    [Fact]
    public void SeparatorInsideText_StillSplitsOnce()
    {
        Assert.Equal(WebAuthRecovery.PageVerdict.Healthy, Classify(Sample(Origin, "a@@DSH@@b"), Origin));
    }

    /// <summary>同源 + 空/纯空白文本 → 未知：页面还没渲染出内容，不判健康。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void BlankText_IsUnknown(string text)
    {
        Assert.Equal(WebAuthRecovery.PageVerdict.Unknown, Classify(Sample(Origin, text), Origin));
    }

    /// <summary>同源 + 鉴权标记（含前后缀包裹形态）→ auth。</summary>
    [Theory]
    [InlineData("dsh web authentication required; reopen the URL printed by dsh web.")]
    [InlineData("xx authentication required yy")]
    public void AuthText_IsAuth(string text)
    {
        Assert.Equal(WebAuthRecovery.PageVerdict.Auth, Classify(Sample(Origin, text), Origin));
    }

    /// <summary>大小写敏感：上游固定小写串，大写变体不误伤。</summary>
    [Fact]
    public void WrongCaseMarker_IsHealthy()
    {
        Assert.Equal(WebAuthRecovery.PageVerdict.Healthy, Classify(Sample(Origin, "Authentication Required"), Origin));
    }

    /// <summary>标记常量就是 401 页原文的子串（上游耦合点声明，防措辞漂移）。</summary>
    [Fact]
    public void MarkerMatchesUpstream401Text()
    {
        Assert.Contains(WebAuthRecovery.AuthRequiredMarker, Auth, StringComparison.Ordinal);
    }

    /// <summary>明细：可拆采样给出 origin 与文本长度（日志层直接消费，不二次拆采样）。</summary>
    [Fact]
    public void Detail_ReportsOriginAndLength()
    {
        WebAuthRecovery.PageVerdictDetail detail =
            WebAuthRecovery.ClassifyDetail(Sample(Origin, "DeepSeek Harness"), Origin);

        Assert.Equal(WebAuthRecovery.PageVerdict.Healthy, detail.Verdict);
        Assert.True(detail.HasSample);
        Assert.Equal(Origin, detail.Origin);
        Assert.Equal("DeepSeek Harness".Length, detail.VisibleTextLength);
    }

    /// <summary>明细：不可拆采样标 HasSample=false、长度 0；可拆但非同源仍带出实际 origin 供排障。</summary>
    [Theory]
    [InlineData(null, false, "")]
    [InlineData("no-separator", false, "")]
    [InlineData("ryn://app" + PageProbeSample.Separator + "text", true, "ryn://app")]
    public void Detail_MarksMissingSample(string? raw, bool hasSample, string origin)
    {
        WebAuthRecovery.PageVerdictDetail detail = WebAuthRecovery.ClassifyDetail(raw, Origin);

        Assert.Equal(WebAuthRecovery.PageVerdict.Unknown, detail.Verdict);
        Assert.Equal(hasSample, detail.HasSample);
        Assert.Equal(origin, detail.Origin);
    }
}
