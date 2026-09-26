namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>Ryn 桥返回值解码（ADR page-verdict-gate）：字符串带引号/转义是桥的真实形态，
/// 夹具必须用桥形态——否则"裸采样单测全绿、线上恒 unknown"这类外壳漂移门禁照不出。</summary>
public class RynProbeValueTests
{
    private const string Sample = "http://127.0.0.1:40683" + Core.PageProbeSample.Separator + "DeepSeek Harness";

    /// <summary>桥真实形态：JSON 字符串字面量 → 还原为原始采样。</summary>
    [Fact]
    public void QuotedJsonString_Decodes()
    {
        string wire = System.Text.Json.JsonSerializer.Serialize(Sample);

        Assert.Equal(Sample, RynProbeValue.Decode(wire));
    }

    /// <summary>转义还原：内嵌引号与换行不残留转义（<c>Trim('"')</c> 做不到这点，固不用它）。</summary>
    [Theory]
    [InlineData("http://x@@DSH@@say \"hi\"", "say \"hi\"")]
    [InlineData("http://x@@DSH@@line1\nline2", "line1\nline2")]
    public void Escapes_Restored(string raw, string text)
    {
        string decoded = RynProbeValue.Decode(System.Text.Json.JsonSerializer.Serialize(raw))!;

        Assert.EndsWith(text, decoded, StringComparison.Ordinal);
    }

    /// <summary>裸值形态（非 JSON）原样返回，不因解码失败丢采样。</summary>
    [Fact]
    public void BareValue_PassesThrough()
    {
        Assert.Equal(Sample, RynProbeValue.Decode(Sample));
    }

    /// <summary>JSON null / 空 / null → null（探针无值，交裁决判未知）。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyOrJsonNull_IsNull(string? raw)
    {
        Assert.Null(RynProbeValue.Decode(raw));
    }
}
