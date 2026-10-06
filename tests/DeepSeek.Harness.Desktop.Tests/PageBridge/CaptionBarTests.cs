namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>CaptionBar 工厂契约（ADR frameless-uniform-caption-bar）：darwin 平台标记、红绿灯三点
/// （无背景面）、幂等 id 守卫、声明式窗口控制属性、可达名双语与转义纪律。</summary>
public class CaptionBarTests
{
    /// <summary>验证 darwin 标记脚本：设置 <c>html[data-platform='darwin']</c>（dsh web 桌面呈现开关）
    /// 并把透明链压回 bg-base（无 vibrancy 窗口上的扁平化）。</summary>
    [Fact]
    public void BuildPlatformMark_SetsDarwinPresentation()
    {
        string script = CaptionBar.BuildPlatformMark();
        Assert.Contains("document.documentElement.setAttribute('data-platform','darwin')", script);
        // css 经 JsString（JSON 编码）后单引号以 \u0027 转义出现
        Assert.Contains("html[data-platform=\\u0027darwin\\u0027] body{background:var(--dsw-alias-bg-base,#fff)!important}", script, StringComparison.Ordinal);
    }

    /// <summary>验证生成脚本带固定 id 的幂等守卫：红绿灯簇已存在（含 index.html 静态簇）时注入直接返回；
    /// 且 darwin 标记在守卫之前设置（首屏呈现不依赖点簇是否注入）。</summary>
    [Fact]
    public void Build_GuardsDoubleInjection_AfterPlatformMark()
    {
        string script = CaptionBar.Build();
        int mark = script.IndexOf("setAttribute('data-platform','darwin')", StringComparison.Ordinal);
        int guard = script.IndexOf("if(document.getElementById(id))return;", StringComparison.Ordinal);
        Assert.True(mark >= 0);
        Assert.True(guard > mark, "darwin 标记必须先于幂等守卫（守卫返回时标记已生效）");
    }

    /// <summary>B1 回归钉：Build（Win/Linux 路）必须自带透明链扁平化——darwin 规则下 html/body 透明、
    /// 侧栏半透 tint，无 vibrancy 窗口不扁平化即暗色主题侧栏洗白。css 经 JsString 后单引号以 \u0027
    /// 转义出现。</summary>
    [Fact]
    public void Build_IncludesTransparencyChainFlattening()
    {
        string script = CaptionBar.Build();
        Assert.Contains("html[data-platform=\\u0027darwin\\u0027] body{background:var(--dsw-alias-bg-base,#fff)!important}", script, StringComparison.Ordinal);
    }

    /// <summary>验证三按钮带 Ryn 声明式窗口控制属性（红绿灯序：关闭/最小化/最大化）、无背景面、
    /// 无内容让位（官方 macOS 形态：内容满幅到顶），按钮本身不带事件处理器。</summary>
    [Fact]
    public void Build_DeclaresTrafficLightControls_NoBand()
    {
        string script = CaptionBar.Build();
        Assert.Contains("\\u003Cbutton data-webview-close\\u003E", script, StringComparison.Ordinal);
        Assert.Contains("\\u003Cbutton data-webview-minimize\\u003E", script, StringComparison.Ordinal);
        Assert.Contains("\\u003Cbutton data-webview-maximize\\u003E", script, StringComparison.Ordinal);
        Assert.Contains("bar.innerHTML=", script);
        // 无色带：不画背景面、不推内容（色带与 dsh 配色不一致的根因，评审第二轮移除）
        Assert.DoesNotContain("padding-top", script, StringComparison.Ordinal);
        Assert.DoesNotContain("position:fixed;top:0;left:0;right:0", script, StringComparison.Ordinal);
    }

    /// <summary>验证红绿灯点色 = macOS 系统色（关闭红/最小化黄/最大化绿），悬停符号深色；
    /// 点簇不消费任何 dsh token（配色一致性问题由「不画面」根治，token 只留在 mark 的透明链扁平化）。</summary>
    [Fact]
    public void Build_TrafficLightColors_AreMacSystemColors()
    {
        string script = CaptionBar.Build();
        Assert.Contains("button[data-webview-close] span{background:#ff5f57}", script, StringComparison.Ordinal);
        Assert.Contains("button[data-webview-minimize] span{background:#febc2e}", script, StringComparison.Ordinal);
        Assert.Contains("button[data-webview-maximize] span{background:#28c840}", script, StringComparison.Ordinal);
        Assert.Contains("button:hover svg{display:block}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("var(--dsw-alias-bg-overlay", script, StringComparison.Ordinal);
    }

    /// <summary>验证 innerHTML 全段经 JsString 双引号管线注入：SVG 图标属性的单引号若裸拼进单引号
    /// JS 字符串会在首个属性引号处语法终止，整个 IIFE 解析即炸（评审 B1 回归钉）。</summary>
    [Fact]
    public void Build_InnerHtmlViaJsString_NoRawSingleQuoteInjection()
    {
        string script = CaptionBar.Build();
        Assert.Contains("bar.innerHTML=\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML='<", script, StringComparison.Ordinal);
    }

    /// <summary>验证按钮可达名随宿主 locale 切换：en 出英文、缺省中文，且中文经 JsString 转义
    /// （读屏可达性不落在硬编码文案上）。</summary>
    [Fact]
    public void Build_AriaLabels_LocalizedViaJsString()
    {
        string zh = CaptionBar.Build();
        Assert.DoesNotContain("最小化", zh, StringComparison.Ordinal);
        Assert.DoesNotContain("关闭", zh, StringComparison.Ordinal);
        // 「最」= U+6700：JsString 转义后的中文可达名（编码器输出大写十六进制）
        Assert.Contains("\\u6700", zh, StringComparison.Ordinal);

        var en = new UiLocale();
        en.Set("en");
        string enScript = CaptionBar.Build(en);
        Assert.Contains("aria-label", enScript, StringComparison.Ordinal);
        Assert.Contains("Minimize", enScript, StringComparison.Ordinal);
        Assert.Contains("Maximize", enScript, StringComparison.Ordinal);
        Assert.Contains("Close", enScript, StringComparison.Ordinal);
    }
}
