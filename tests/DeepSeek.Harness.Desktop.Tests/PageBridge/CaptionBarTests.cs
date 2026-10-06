namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>CaptionBar 工厂契约（ADR frameless-uniform-caption-bar）：壳自绘 macOS 观感（红绿灯 +
/// 侧栏顶部让位 + 宿主 chrome 高度变量）、幂等守卫、可达名双语与转义纪律；并钉死「禁冒充 dsh 桌面宿主」红线。</summary>
public class CaptionBarTests
{
    /// <summary>红线回归钉（v0.6.1/0.6.2 启动失败根因）：注入脚本<b>绝不</b>设置 <c>data-platform</c>——
    /// 该属性是 dsh 客户端的桌面运行时开关，见之即要求官方 Electron 的 <c>window.dshDesktop</c> 桥，
    /// <c>dsh-client-shortcuts</c> 随即抛错并级联 25 个 UI 插件 pending（启动即插件加载失败屏）。</summary>
    [Fact]
    public void Build_NeverSetsDataPlatform()
    {
        string script = CaptionBar.Build(52);
        Assert.DoesNotContain("data-platform", script, StringComparison.Ordinal);
    }

    /// <summary>红线之二：不设 <c>data-windows-titlebar</c>（那是 dsh 的 Windows 呈现开关，非本需求形态）、
    /// 不注入 <c>dshDesktop</c> 假桥。</summary>
    [Fact]
    public void Build_DoesNotFakeOtherHosts()
    {
        string script = CaptionBar.Build(52);
        Assert.DoesNotContain("data-windows-titlebar", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dshDesktop", script, StringComparison.Ordinal);
    }

    /// <summary>验证折叠按钮回到让位带右端（官方 darwin 布局把折叠按钮放顶条里、与红绿灯同行）：
    /// 同批放开品牌行裁剪（不放开则按钮几何到位却画不出来——真实 DOM 实测踩过），且不新增自绘按钮
    /// （会与原生重复——同样实测踩过）；折叠态把裁剪与位移一并还原，按钮留在轨内可点。
    /// 断言用无引号子串：CSS 经 JsString（JSON 编码）后选择器里的引号会转义。</summary>
    [Fact]
    public void Build_AlignsSidebarToggleIntoStrip()
    {
        string script = CaptionBar.Build(52);
        Assert.Contains("--dsh-desk-caption-h:52px", script, StringComparison.Ordinal);
        // 无引号也无加号的子串：JsString 会把 " 与 + 一并转义（\u0022 / \u002B）
        Assert.Contains("translateY(calc(-1 * (var(--dsh-desk-caption-h)", script, StringComparison.Ordinal);
        Assert.Contains("transform:none}", script, StringComparison.Ordinal);
        Assert.Contains("overflow:visible!important}", script, StringComparison.Ordinal);
        Assert.Contains("overflow:hidden!important}", script, StringComparison.Ordinal);
        // 不新增自绘折叠按钮（会与原生重复）：脚本里不得出现额外的按钮簇元素 id
        Assert.DoesNotContain("caption-toggle", script, StringComparison.Ordinal);
    }

    /// <summary>验证折叠态三点收进窄轨（官方 macOS 折叠是整列隐藏、无窄轨；窄轨是 dsh 非 darwin 布局，
    /// 我们的灯必须随之收进轨内，否则会漂到内容区上）：内缩/间距/直径都缩小为轨宽内可容。</summary>
    [Fact]
    public void Build_PacksTrafficLightsIntoRailWhenCollapsed()
    {
        string script = CaptionBar.Build(52);
        Assert.Contains("html:has(", script, StringComparison.Ordinal);
        Assert.Contains("padding:20px 0 0 10px;gap:6px}", script, StringComparison.Ordinal);
        Assert.Contains("button{width:10px;height:10px}", script, StringComparison.Ordinal);
    }

    /// <summary>验证宿主 chrome 高度登进 dsh 两枚公开变量（top-clearance 由客户端 JS 无条件读取让浮层避开；
    /// chrome-top 供遮罩消费），且侧栏顶部让位与红绿灯带同高——padding 属侧栏自身背景盒，让位带被其填充色
    /// 无缝覆盖（macOS 观感的来源）。</summary>
    [Theory]
    [InlineData(52)]
    [InlineData(48)]
    public void Build_PublishesChromeHeight_AndSidebarClearance(int height)
    {
        string script = CaptionBar.Build(height);
        Assert.Contains($"--dsh-frame-top-clearance:{height}px", script, StringComparison.Ordinal);
        Assert.Contains($"--dsh-frame-chrome-top:{height}px", script, StringComparison.Ordinal);
        Assert.Contains("sidebarCol", script, StringComparison.Ordinal);
        Assert.Contains($"padding-top:{height}px!important", script, StringComparison.Ordinal);
        Assert.Contains($"#dsh-desktop-caption-bar{{position:fixed;top:0;left:0;height:{height}px", script, StringComparison.Ordinal);
    }

    /// <summary>验证红绿灯三点＝macOS 系统色（关闭红/最小化黄/最大化绿）、悬停出深色符号、
    /// 位置对齐官方 <c>trafficLightPosition(16,18)</c>；按钮带 Ryn 声明式窗口控制属性且无事件处理器。</summary>
    [Fact]
    public void Build_DrawsMacTrafficLights()
    {
        string script = CaptionBar.Build(52);
        Assert.Contains("button[data-webview-close]{background:#ff5f57}", script, StringComparison.Ordinal);
        Assert.Contains("button[data-webview-minimize]{background:#febc2e}", script, StringComparison.Ordinal);
        Assert.Contains("button[data-webview-maximize]{background:#28c840}", script, StringComparison.Ordinal);
        Assert.Contains("padding:18px 0 0 16px", script, StringComparison.Ordinal);
        Assert.Contains("button:hover svg{display:block}", script, StringComparison.Ordinal);
        Assert.Contains("\\u003Cbutton data-webview-close\\u003E", script, StringComparison.Ordinal);
        Assert.Contains("\\u003Cbutton data-webview-minimize\\u003E", script, StringComparison.Ordinal);
        Assert.Contains("\\u003Cbutton data-webview-maximize\\u003E", script, StringComparison.Ordinal);
    }

    /// <summary>验证样式注入带查重守卫 + 元素 id 幂等守卫（双路注入不重复追加）。</summary>
    [Fact]
    public void Build_GuardsDoubleInjection()
    {
        string script = CaptionBar.Build(52);
        Assert.Contains("if(document.getElementById(id))return;", script, StringComparison.Ordinal);
        Assert.Contains("querySelector(\"style[data-dsh-desktop='caption-bar']\")", script, StringComparison.Ordinal);
    }

    /// <summary>验证 innerHTML 全段经 JsString 双引号管线注入：SVG 图标属性的单引号若裸拼进单引号
    /// JS 字符串会在首个属性引号处语法终止，整个 IIFE 解析即炸（FULL 评审 B1 回归钉）。</summary>
    [Fact]
    public void Build_InnerHtmlViaJsString()
    {
        string script = CaptionBar.Build(52);
        Assert.Contains("bar.innerHTML=\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML='<", script, StringComparison.Ordinal);
    }

    /// <summary>验证按钮可达名随宿主 locale 切换：en 出英文、缺省中文经 JsString 转义
    /// （读屏可达性不落在硬编码文案上）。</summary>
    [Fact]
    public void Build_AriaLabels_LocalizedViaJsString()
    {
        string zh = CaptionBar.Build(52);
        Assert.DoesNotContain("最小化", zh, StringComparison.Ordinal);
        Assert.DoesNotContain("关闭", zh, StringComparison.Ordinal);
        // 「最」= U+6700：JsString 转义后的中文可达名（编码器输出大写十六进制）
        Assert.Contains("\\u6700", zh, StringComparison.Ordinal);

        var en = new UiLocale();
        en.Set("en");
        string enScript = CaptionBar.Build(52, en);
        Assert.Contains("Minimize", enScript, StringComparison.Ordinal);
        Assert.Contains("Maximize", enScript, StringComparison.Ordinal);
        Assert.Contains("Close", enScript, StringComparison.Ordinal);
    }
}
