namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>CaptionBar 工厂契约（ADR frameless-uniform-caption-bar）：壳自绘 macOS 观感（红绿灯 +
/// 侧栏顶部让位 + 宿主 chrome 高度变量）、幂等守卫、顶层文档守卫、带内拖拽判定（判定权收归本壳）、
/// 可达名双语与转义纪律；并钉死「禁冒充 dsh 桌面宿主」红线。</summary>
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

    /// <summary>验证宿主 chrome 高度登进 dsh 公开变量：top-clearance 随高度发布（客户端 JS 无条件读取
    /// 让布局/浮层定位避开）；chrome-top 恒为 0——上游该变量只为窗外原生 caption 留空，本壳顶带是窗内
    /// 页面（tab 条在内），随高度发布会把顶带排除在模态遮罩绘制之外。侧栏顶部让位与红绿灯带同高——
    /// padding 属侧栏自身背景盒，让位带被其填充色无缝覆盖（macOS 观感的来源）。</summary>
    [Theory]
    [InlineData(52)]
    [InlineData(48)]
    public void Build_PublishesChromeHeight_AndSidebarClearance(int height)
    {
        string script = CaptionBar.Build(height);
        Assert.Contains($"--dsh-frame-top-clearance:{height}px", script, StringComparison.Ordinal);
        Assert.Contains("--dsh-frame-chrome-top:0px", script, StringComparison.Ordinal);
        Assert.Contains("sidebarCol", script, StringComparison.Ordinal);
        Assert.Contains($"padding-top:{height}px!important", script, StringComparison.Ordinal);
        // 结构式兜底收窄（评审 S3）：:where() 归零权重 + 父级须有第二个孩子——侧栏列一旦挪位只会
        // 不命中（顶区无让位），不会把让位间距误打到内容列首元素上
        Assert.Contains(":where(:has(", script, StringComparison.Ordinal);
        Assert.Contains(":nth-child(2))", script, StringComparison.Ordinal);
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
        // 注入条必须保持可命中（回归钉）：三个圆点的点击落点在这条自绘条上，设 pointer-events:none 会
        // 让三键点不到（顶带拖拽面已由本壳自持判据承担，见 Build_OwnsTopBandDragJudgement 与 ADR）。
        Assert.DoesNotContain("pointer-events:none", script, StringComparison.Ordinal);
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

    /// <summary>可达名：两套语言都进脚本、执行期按 <c>html[lang]</c> 现取（注册式脚本由引擎在<b>每次页面
    /// 加载</b>时执行，构建期语言不再是执行期语言）；<c>html[lang]</c> 缺失时回落宿主当时 locale——该参数的
    /// 真实作用点就是这一处回落布尔（英文集恒在脚本里，故「含 Minimize」不再能判 locale）。字面量单一
    /// 事实源仍是 UiCopy：中文经 JsString 转义，不以裸 CJK 出现。</summary>
    /// <param name="hostEnglish">宿主 locale 是否英文（null locale 即缺省中文）。</param>
    /// <param name="hostFallback">期望写进脚本的回落布尔字面量。</param>
    [Theory]
    [InlineData(false, "false")]
    [InlineData(true, "true")]
    public void Build_AriaLabels_TwoLocaleSetsWithDocumentPick(bool hostEnglish, string hostFallback)
    {
        UiLocale? locale = null;
        if (hostEnglish)
        {
            locale = new UiLocale();
            locale.Set("en");
        }

        string script = CaptionBar.Build(52, locale);
        Assert.Contains("var L={zh:[", script, StringComparison.Ordinal);
        Assert.Contains("en:[", script, StringComparison.Ordinal);
        Assert.Contains("Minimize", script, StringComparison.Ordinal);
        Assert.Contains("var lg=document.documentElement.lang||'';", script, StringComparison.Ordinal);
        Assert.Contains($"indexOf('en')===0):{hostFallback})?L.en:L.zh;", script, StringComparison.Ordinal);
        // 中文可达名经 JsString 转义（「最」= U+6700）——构建期不落裸 CJK 字面量
        Assert.DoesNotContain("最小化", script, StringComparison.Ordinal);
        Assert.DoesNotContain("关闭", script, StringComparison.Ordinal);
        Assert.Contains("\\u6700", script, StringComparison.Ordinal);
    }

    /// <summary>macOS 绿灯＝原生全屏（官方 macOS 绿灯是 Enter Full Screen，窗口缩放另有其键）：
    /// 换成本壳自有属性 + 自绑点击走 window.setFullscreen，并不再出现 data-webview-maximize——
    /// 后者是 Ryn 注入脚本的窗口缩放语义，留着会与原生全屏并存且语义相左；其他平台保持缩放不变。</summary>
    [Fact]
    public void Build_MacChrome_GreenButtonEntersNativeFullscreen()
    {
        var en = new UiLocale();
        en.Set("en");
        string mac = CaptionBar.Build(52, en, macChrome: true);
        Assert.Contains("data-dsh-fullscreen", mac, StringComparison.Ordinal);
        Assert.Contains("button[data-dsh-fullscreen]{background:#28c840}", mac, StringComparison.Ordinal);
        Assert.Contains("window.setFullscreen", mac, StringComparison.Ordinal);
        Assert.Contains("Full Screen", mac, StringComparison.Ordinal);
        Assert.Contains("bar.children[2].addEventListener('click'", mac, StringComparison.Ordinal);
        // 缩放语义不得出现在本壳三点的标记或样式里（注入条的交互排除表里出现属性名不算——
        // 那是与 Ryn 同款的语义表，不是本壳按钮的声明）
        Assert.DoesNotContain("button[data-webview-maximize]", mac, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003Cbutton data-webview-maximize\\u003E", mac, StringComparison.Ordinal);
        // 透明链只此一支：mac 脚本同样不得关掉注入条的命中（拖拽面回归钉，见红绿灯用例）
        Assert.DoesNotContain("pointer-events:none", mac, StringComparison.Ordinal);

        // 非 macOS 不得出现全屏语义（缩放面已由既有红绿灯用例钉住，此处只钉「不越界」）
        string other = CaptionBar.Build(52, en);
        Assert.DoesNotContain("data-dsh-fullscreen", other, StringComparison.Ordinal);
        Assert.DoesNotContain("window.setFullscreen", other, StringComparison.Ordinal);
    }

    /// <summary>macOS 毛玻璃透明链（官方 vibrancy 观感）：窗/网页背景由 RynOptions.Backdrop 清成透明，
    /// 页面侧须让 html/body 透明、侧栏列半透明，材质才透得出来；官方用 html[data-platform='darwin']
    /// 门控，而该属性正是本壳禁用的宿主冒充开关（插件崩），故按平台在注入期取舍——非 macOS 恒不含
    /// 透明链（无 backdrop 后端时会直接露底）。</summary>
    [Fact]
    public void Build_MacChrome_AddsVibrancyTransparencyChain()
    {
        string mac = CaptionBar.Build(52, macChrome: true);
        Assert.Contains("html,body{background:transparent!important}", mac, StringComparison.Ordinal);
        Assert.Contains(
            "color-mix(in srgb,var(--dsw-alias-bg-base,#fff) 80%,transparent)!important", mac, StringComparison.Ordinal);

        string other = CaptionBar.Build(52);
        Assert.DoesNotContain("background:transparent", other, StringComparison.Ordinal);
        Assert.DoesNotContain("color-mix(", other, StringComparison.Ordinal);
    }

    /// <summary>顶层文档守卫：同一份脚本还经 Ryn 的 <c>InjectScriptAsync</c> 注册为「每次页面加载执行」，
    /// 而该注册按 no_frames=false 注入<b>所有 frame</b>；没有这条守卫时 dsh 的每个层级子文档都会长出
    /// 一条壳顶栏。</summary>
    [Fact]
    public void Build_GuardsTopFrameOnly()
    {
        Assert.Contains("if(window.top!==window.self)return;", CaptionBar.Build(52), StringComparison.Ordinal);
    }

    /// <summary>顶带拖拽判定收归本壳（回归钉）：Ryn 的自动拖拽条要求命中元素底边（bottom）≤ strip×1.5，
    /// 而 dsh 把整条顶带留给自顶带起算的流内 chrome（侧栏 padding 区、<b>会话头部</b>）——该判据在我们这条带上
    /// 不成立。四条接管判据（带内 / 非 html·body / 非交互 / 盒顶在带内起算且上溯流内）与两条 capture 监听
    /// 缺一即退化回「整条拖不动」；按盒高判「全高」会把会话头部（<c>min-height:76px</c>）漏掉，中间内容列
    /// 因此整条不动——故此处钉的是<b>盒顶</b>判据。</summary>
    [Fact]
    public void Build_OwnsTopBandDragJudgement()
    {
        string script = CaptionBar.Build(52);
        Assert.Contains("var bandClaim=false,lastDown=0,lastX=0,lastY=0;", script, StringComparison.Ordinal);
        Assert.Contains("function bandPoint(e){", script, StringComparison.Ordinal);
        Assert.Contains("if(e.clientY>52)return false;", script, StringComparison.Ordinal);
        Assert.Contains("if(t===document.documentElement||t===document.body)return false;", script, StringComparison.Ordinal);
        Assert.Contains(
            "if(t.closest(NODRAG)||t.closest('[data-webview-drag]'))return false;", script, StringComparison.Ordinal);
        Assert.Contains("if(t.getBoundingClientRect().top>52)return false;", script, StringComparison.Ordinal);
        // 定位元素（浮层/模态/列宽手柄）仍按内容处理；唯一例外是本壳自绘条自身（条内非按钮区域算壳带）。
        Assert.Contains("if(p==='fixed'||p==='absolute')return n.id===id;", script, StringComparison.Ordinal);
        Assert.Contains("[data-webview-ignore]", script, StringComparison.Ordinal);
        // dsh 自己声明的 darwin no-drag 簇（会话头部按钮与角落座位）随 INTERACTIVE 一并让行。
        Assert.Contains(
            "var NODRAG=INTERACTIVE+',[class*=\"headerLeading\"],[class*=\"headerActions\"]," +
            "[class*=\"headerUtilities\"],[class*=\"headerCorner\"]';",
            script,
            StringComparison.Ordinal);
        Assert.Contains("e.defaultPrevented", script, StringComparison.Ordinal);
        Assert.Contains("bandClaim=true;", script, StringComparison.Ordinal);
        // 承重不变量：claim 的复位须在回调最前（先于 defaultPrevented/bandPoint 的早退），置真须在双击
        // 自判之后——双击路一旦命中就 return 且本轮不复位 claim，DOM 的 dblclick 因此不再二次派发。
        Assert.Contains("document.addEventListener('mousedown',function(e){bandClaim=false;", script, StringComparison.Ordinal);
        Assert.Contains(
            "e.preventDefault();if(dbl){w.invoke('window.toggleMaximize');return;}bandClaim=true;",
            script,
            StringComparison.Ordinal);
        Assert.Contains("document.addEventListener('dblclick',function(e){", script, StringComparison.Ordinal);
        Assert.Contains("w.invoke('window.startDrag');", script, StringComparison.Ordinal);
        Assert.Contains("w.invoke('window.beginNativeDrag',{x:e.clientX,y:e.clientY});", script, StringComparison.Ordinal);
        // 判据上界跟配置高度走（禁硬编码 52）
        Assert.Contains("if(e.clientY>48)return false;", CaptionBar.Build(48), StringComparison.Ordinal);
        Assert.Contains(
            "if(t.getBoundingClientRect().top>48)return false;", CaptionBar.Build(48), StringComparison.Ordinal);
    }

    /// <summary>双击缩放自判（回归钉）：本壳 mousedown 起的原生窗口移动由合成器隐式抓取承接，随后的
    /// click/dblclick 序列不再到达页面——等 DOM 的 <c>dblclick</c> 就等于顶带永远不缩放。故双击按两次
    /// 带内左键按下的间隔/位移自判，自判窗取各平台双击窗上界（<see cref="CaptionBar.DoubleClickMs"/>）。</summary>
    [Fact]
    public void Build_SelfJudgesDoubleClickOnSecondPress()
    {
        string script = CaptionBar.Build(52);
        // 取值不手抄：判据是「生产常量进了脚本」（唯一事实源在 CaptionBar）。
        Assert.Contains(
            $"var DBL_MS={CaptionBar.DoubleClickMs},DBL_SLOP={CaptionBar.DoubleClickSlopPx};",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "var dbl=now-lastDown<=DBL_MS&&Math.abs(e.clientX-lastX)<=DBL_SLOP&&" +
            "Math.abs(e.clientY-lastY)<=DBL_SLOP;",
            script,
            StringComparison.Ordinal);
        // 命中双击即清账（三连击重新起算），未命中则记下本轮点位作下一次的比较基准。
        Assert.Contains("lastDown=dbl?0:now;lastX=e.clientX;lastY=e.clientY;", script, StringComparison.Ordinal);
        // 承重不变量（R2-S2）：双击分支的整段内容——派发后立刻 return，分支内不置 claim；claim 的置真
        // 只可能在分支之后的起拖路上。挪成「先置 claim 再判 dbl」即在此拦下（净零双派发回归）。
        Assert.Contains(
            "e.preventDefault();if(dbl){w.invoke('window.toggleMaximize');return;}bandClaim=true;",
            script,
            StringComparison.Ordinal);
        // 兜底路的 claim 闸门（同一不变量只在此钉一次，见 Build_OwnsTopBandDragJudgement）。
        Assert.Contains("if(!bandClaim||!bandPoint(e))return;", script, StringComparison.Ordinal);
    }
}
