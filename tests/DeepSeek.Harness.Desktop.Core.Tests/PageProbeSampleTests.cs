namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>探针采样拆分（ADR page-verdict-gate）：只按首个分隔符切，残缺即 false。</summary>
public class PageProbeSampleTests
{
    /// <summary>常规两字段拆分：origin 在前，文本可含换行与同串。</summary>
    [Fact]
    public void SplitsOnFirstSeparator()
    {
        Assert.True(PageProbeSample.TrySplit(
            "http://127.0.0.1:40683" + PageProbeSample.Separator + "line1\nline2@@DSH@@tail",
            out string origin,
            out string text));

        Assert.Equal("http://127.0.0.1:40683", origin);
        Assert.Equal("line1\nline2@@DSH@@tail", text);
    }

    /// <summary>分隔符之后为空文本 → 拆分成功但文本为空（裁决那边判未知）。</summary>
    [Fact]
    public void EmptyTextAfterSeparator_Splits()
    {
        Assert.True(PageProbeSample.TrySplit("ryn://app" + PageProbeSample.Separator, out string origin, out string text));
        Assert.Equal("ryn://app", origin);
        Assert.Equal(string.Empty, text);
    }

    /// <summary>残缺采样一律 false：null/空串/无分隔符/源为空。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://127.0.0.1:40683")]
    [InlineData("@@DSH@@text")]
    public void MalformedSamples_ReturnFalse(string? raw)
    {
        Assert.False(PageProbeSample.TrySplit(raw, out _, out _));
    }

    /// <summary>分隔符必须能原样嵌进单引号 JS 字面量（探针脚本编译期拼入）：纯 ASCII 可打印，且不含引号/反斜杠/控制符。</summary>
    [Fact]
    public void Separator_IsJsLiteralSafe()
    {
        Assert.NotEmpty(PageProbeSample.Separator);
        Assert.DoesNotContain('\'', PageProbeSample.Separator);
        Assert.DoesNotContain('\\', PageProbeSample.Separator);
        Assert.All(PageProbeSample.Separator, c => Assert.True(char.IsAscii(c) && !char.IsControl(c), $"非法字符 U+{(int)c:X4}"));
    }
}
