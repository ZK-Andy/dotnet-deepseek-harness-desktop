namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>DshWebUrl branded 端点契约（ADR composition-root-value-flow-pipeline 批次 2）：
/// 工厂构造、origin/根派生与空实例 fail loud（default 不可用）。</summary>
public class DshWebUrlTests
{
    /// <summary>From 包装非空 URL；Value 原样、Authority 取 origin、AuthorityRoot 带尾斜杠、ToString 即 URL。</summary>
    [Fact]
    public void From_DerivesOriginAndRoot()
    {
        var url = DshWebUrl.From(new Uri("http://127.0.0.1:33019/ui?x=1"));

        Assert.Equal("http://127.0.0.1:33019/ui?x=1", url.Value.ToString());
        Assert.Equal("http://127.0.0.1:33019", url.Authority);
        Assert.Equal("http://127.0.0.1:33019/", url.AuthorityRoot.ToString());
        Assert.Equal("http://127.0.0.1:33019/ui?x=1", url.ToString());
    }

    /// <summary>From(null) fail loud；FromNullable(null) 保持 null（宿主未在时限内给出 URL 的形态）。</summary>
    [Fact]
    public void Factories_SeparateNullSemantics()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = DshWebUrl.From(null!); });
        Assert.Null(DshWebUrl.FromNullable(null));
        Assert.NotNull(DshWebUrl.FromNullable(new Uri("http://127.0.0.1:1/")));
    }

    /// <summary>空实例（default/无参构造）访问 Value/Authority/ToString 一律具名 fail loud，不落 NRE。</summary>
    [Fact]
    public void Default_Access_FailsLoud()
    {
        DshWebUrl empty = default;

        Assert.Throws<InvalidOperationException>(() => { _ = empty.Value; });
        Assert.Throws<InvalidOperationException>(() => { _ = empty.Authority; });
        Assert.Throws<InvalidOperationException>(() => { _ = empty.ToString(); });
    }
}
