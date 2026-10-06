namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>CaptionBar 工厂契约（ADR frameless-uniform-caption-bar）：幂等 id 守卫、声明式窗口控制属性、
/// 布局让位规则、主题 token 消费与可达名双语。</summary>
public class CaptionBarTests
{
    /// <summary>验证生成脚本带固定 id 的幂等守卫：顶栏已存在（含 index.html 静态条）时注入直接返回，
    /// 不重复加 style 与 body 让位。</summary>
    [Fact]
    public void Build_GuardsDoubleInjection()
    {
        string script = CaptionBar.Build(40);
        Assert.Contains("var id='dsh-desktop-caption-bar'", script);
        Assert.Contains("if(document.getElementById(id))return;", script);
    }

    /// <summary>验证三按钮带 Ryn 声明式窗口控制属性、整条可拖（data-webview-drag），
    /// 按钮本身不带任何事件处理器（点击语义全由 Ryn 注入脚本委托）。按钮标签经 JsString 后
    /// <c>&lt;</c>/<c>&gt;</c> 以 \u003C/\u003E 转义出现（JSON 编码语义）。</summary>
    [Fact]
    public void Build_DeclaresWindowControlAttributes()
    {
        string script = CaptionBar.Build(40);
        Assert.Contains("bar.setAttribute('data-webview-drag','')", script);
        Assert.Contains("\\u003Cbutton data-webview-minimize\\u003E", script, StringComparison.Ordinal);
        Assert.Contains("\\u003Cbutton data-webview-maximize\\u003E", script, StringComparison.Ordinal);
        Assert.Contains("\\u003Cbutton data-webview-close\\u003E", script, StringComparison.Ordinal);
        Assert.Contains("bar.innerHTML=", script);
    }

    /// <summary>验证 innerHTML 全段经 JsString 双引号管线注入：SVG 图标属性的单引号若裸拼进单引号
    /// JS 字符串会在首个属性引号处语法终止，整个 IIFE 解析即炸（评审 B1 回归钉）。</summary>
    [Fact]
    public void Build_InnerHtmlViaJsString_NoRawSingleQuoteInjection()
    {
        string script = CaptionBar.Build(40);
        Assert.Contains("bar.innerHTML=\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML='<", script, StringComparison.Ordinal);
    }

    /// <summary>验证高度参数贯通两处几何：顶栏 height 与 body padding-top（让位）取同值。</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(48)]
    public void Build_HeightFlowsToBarAndOffset(int height)
    {
        string script = CaptionBar.Build(height);
        Assert.Contains($"height:{height}px;", script);
        Assert.Contains($"body{{padding-top:{height}px!important;box-sizing:border-box!important}}", script);
    }

    /// <summary>验证配色主路消费 dsh 主题 token（随页面明暗），回退值经自定义属性接官方
    /// prefers-color-scheme 规格（亮 #f9fafb/#0f1115，暗 #1b1b1c/#f9fafb）。</summary>
    [Fact]
    public void Build_ConsumesThemeTokens_WithOfficialFallbacks()
    {
        string script = CaptionBar.Build(40);
        Assert.Contains("background:var(--dsw-alias-bg-overlay,var(--cap-fb-bg))", script);
        Assert.Contains("color:var(--dsw-alias-label-primary,var(--cap-fb-fg))", script);
        Assert.Contains("--cap-fb-bg:#f9fafb;--cap-fb-fg:#0f1115", script);
        Assert.Contains("@media(prefers-color-scheme:dark){#dsh-desktop-caption-bar{--cap-fb-bg:#1b1b1c;--cap-fb-fg:#f9fafb}}", script);
        Assert.Contains("button[data-webview-close]:hover{background:#e81123;color:#fff}", script);
    }

    /// <summary>验证按钮可达名随宿主 locale 切换：en 出英文、缺省中文，且中文经 JsString 转义
    /// （读屏可达性不落在硬编码文案上）。</summary>
    [Fact]
    public void Build_AriaLabels_LocalizedViaJsString()
    {
        string zh = CaptionBar.Build(40);
        Assert.DoesNotContain("最小化", zh, StringComparison.Ordinal);
        Assert.DoesNotContain("关闭", zh, StringComparison.Ordinal);
        // 「最」= U+6700：JsString 转义后的中文可达名（编码器输出大写十六进制）
        Assert.Contains("\\u6700", zh, StringComparison.Ordinal);

        var en = new UiLocale();
        en.Set("en");
        string enScript = CaptionBar.Build(40, en);
        Assert.Contains("aria-label", enScript, StringComparison.Ordinal);
        Assert.Contains("Minimize", enScript, StringComparison.Ordinal);
        Assert.Contains("Maximize", enScript, StringComparison.Ordinal);
        Assert.Contains("Close", enScript, StringComparison.Ordinal);
    }
}
