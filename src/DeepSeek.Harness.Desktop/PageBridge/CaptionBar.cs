using DeepSeek.Harness.Desktop.Core.Ipc;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 无边框窗口 chrome 注入脚本的薄渲染器：官方 macOS 观感由<b>壳自己</b>实现——侧栏列顶部让位
/// （padding 属元素自身背景盒，让位带被侧栏填充色无缝覆盖）、左上角红绿灯三点（壳绘制，点击走
/// <c>window.__shell.invoke</c>）、并发布 dsh 的三变量几何。几何语义归 Core 纯函数
/// （<see cref="Core.WindowChrome.ChromeInsets"/>，ADR window-chrome-depatch-core-ports）：
/// <c>top-clearance</c> 承担布局让位、<c>chrome-top</c> 恒为 0
/// 使模态遮罩画满全视口（上游该变量只为窗外原生 caption 留空）、<c>overlay-top</c> 让对话框/菜单
/// 避开顶带（台阶 + 20px）。本类只渲染不计算；<c>data-fullscreen</c> 静态不发（运行时状态）。
/// </summary>
/// <remarks>
/// <para><b>禁止设置 <c>data-platform</c></b>（v0.6.1/0.6.2 启动失败根因）：该属性是 dsh 客户端的
/// <i>桌面运行时开关</i>——<c>detectEnvironment</c> 见之即判 <c>runtime="desktop"</c>（官方 Electron
/// 宿主语义），<c>dsh-client-shortcuts</c> 随即要求 <c>window.dshDesktop.keyboard</c>（官方 preload 桥）
/// 并 <c>throw</c>，级联 25 个 UI 插件 pending → 启动即插件加载失败屏。同理不设
/// <c>data-windows-titlebar</c>（那会切到 dsh 的 Windows 呈现，非本需求形态），也不注入任何
/// <c>window.dshDesktop</c> 假桥——本工厂只做宿主本职：量自己的 chrome、画自己的按钮。
/// 官方 macOS 的毛玻璃透明链同样以该属性门控；本壳不注入透明链（窗口侧 backdrop 半边不存在，
/// 见 <see cref="BuildCss"/> 注），macOS 平台差异只剩绿灯语义，在注入期按 <c>macChrome</c> 取舍。</para>
/// <para>三键各自绑 <c>onclick</c>（重复注入时覆盖而非叠监听），统一走 <c>window.__shell.invoke</c>；
/// macOS 绿灯是原生全屏语义，改调 <c>window.setFullscreen</c>。</para>
/// <para><b>注入时机两路互补</b>（ADR frameless-uniform-caption-bar「注入时机」条、avalonia-g2-shell-bridge）：
/// ① 导航完成（内容提交）后重挂——顶栏 DOM 随整页导航销毁，每次到达都须重挂；② 窗口确定启用顶栏时
/// 对当前文档立即注入一次（导航钩子只对<b>后续</b>文档生效）。两路共用同一份脚本：语言由执行期按
/// <c>html[lang]</c> 现取，构建期 locale 只是该属性缺失时的回落；脚本自带幂等守卫，叠加只建一条。</para>
/// <para><b>顶带拖拽判定收归本壳</b>（见 <see cref="BuildDragBinding"/>）：原生 WebView 会消费顶带内的
/// 指针按下（页面收不到事件），且 dsh 把整条顶带留给流内 chrome（列容器与会话头部），故本条带挂自持判据；
/// 双击缩放同由该判据自判（本壳起拖的原生移动抓取会吞掉 DOM 的 click/dblclick 序列【推断 · 未证：机制面
/// 在合成器，本机只有「拖拽管用、双击不缩放」的现象级证据】）。</para>
/// </remarks>

internal static class CaptionBar
{
    /// <summary>红绿灯簇元素 id：幂等守卫依据；wwwroot/index.html 静态簇使用同一 id
    /// （先渲染者胜，注入脚本遇之即让位）。</summary>
    public const string ElementId = "dsh-desktop-caption-bar";

    /// <summary>窗口边缘描边环元素 id（ADR window-edge-ring）：独立幂等守卫，不复用顶栏守卫——
    /// 顶栏守卫命中即整段返回，描边环须有自己的存在性判断，否则顶栏先建的文档永远长不出环。</summary>
    public const string EdgeRingId = "dsh-desktop-edge-ring";

    /// <summary>绿灯按钮属性（macOS）：本壳自有名——原生全屏与窗口缩放语义不同，故换名并自绑点击
    /// （语义见 <see cref="FullscreenClickBinding"/>）。</summary>
    private const string MacGreenAttr = "data-dsh-fullscreen";

    /// <summary>绿灯按钮属性（闸外，含非 macOS 与 Rosetta-x64）：窗口缩放语义。</summary>
    private const string ZoomGreenAttr = "data-webview-maximize";

    /// <summary>dsh 三变量协议名（上游公开量；语义归 <see cref="Core.WindowChrome.ChromeInsets"/>，
    /// 本类只做渲染——协议改名只改此处）。</summary>
    private const string TopClearanceVar = "--dsh-frame-top-clearance";
    private const string ChromeTopVar = "--dsh-frame-chrome-top";
    private const string OverlayTopVar = "--dsh-frame-overlay-top";

    /// <summary>本壳自有变量（供下方 calc 复用；非上游协议）。</summary>
    private const string DeskCaptionHVar = "--dsh-desk-caption-h";

    /// <summary>生成注入脚本（除 macOS 绿灯语义一项差异外三端同款，纯函数可单测）：三变量几何（由 Core <c>ChromeInsets.Resolve</c> 解出） +
    /// 侧栏顶部让位 + 顶层文档守卫 + 幂等守卫 + 红绿灯三按钮 + 带内拖拽判定。可达名（aria-label）双语
    /// 经 <see cref="AppJsonContext.JsString"/> 管线转义注入，执行期按 <c>html[lang]</c> 现取；
    /// innerHTML 全段同样走 JsString（SVG 单引号裸拼会炸解析——FULL 评审 B1）。
    /// macChrome 分支另挂原生全屏点击处理。两条注入路径共用本函数（见类注）。</summary>
    /// <param name="heightPx">chrome 高度（CSS 像素，<see cref="Infrastructure.Runtime.CaptionBarOptions.HeightPx"/>；
    /// 默认 52 = 官方 macOS 侧栏顶条）。</param>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文，ADR host-ui-locale）：仅作 <c>html[lang]</c>
    /// 缺失时的回落值，文档自带语言时以文档为准。</param>
    /// <param name="macChrome">是否启用原生 macOS chrome（调用点传 <see cref="MacNativeChrome.IsEnabled"/>，
    /// 理由与闸界见其类注）：true 时绿灯走<i>原生全屏</i>而非窗口缩放。否则恒 false。</param>
    public static string Build(
        int heightPx,
        UiLocale? uiLocale = null,
        bool macChrome = false,
        bool edgeResize = true)
    {
        bool english = uiLocale?.IsEnglish == true;
        // 两套语言的按钮可达名都进脚本：脚本注册后由引擎在**每次页面加载**时执行（注入时机见类注），
        // 执行期语言只能从文档取（dsh 把语言写在 html[lang]，companion 上报同源），故不在构建期钉死；
        // html[lang] 缺失时回落到宿主当时 locale（构建期取值）。文案字面量单一事实源仍是 UiCopy。
        (string minimizeZh, string maximizeZh, string closeZh) = UiCopy.CaptionButtonNames(english: false);
        (string minimizeEn, string maximizeEn, string closeEn) = UiCopy.CaptionButtonNames(english: true);
        // 绿灯属性与可达名同源切换：绿底 CSS、innerHTML、aria 三处同源该变量（点击处理按第三子位置直绑，
        // 不取属性），避免「属性对、语义错」。
        string greenAttr = macChrome ? MacGreenAttr : ZoomGreenAttr;
        string greenZh = macChrome ? UiCopy.CaptionFullscreenName(english: false) : maximizeZh;
        string greenEn = macChrome ? UiCopy.CaptionFullscreenName(english: true) : maximizeEn;
        // 三变量几何归 Core 纯函数（ADR window-chrome-depatch-core-ports）：本壳无全屏状态源，
        // 恒按非全屏解；data-fullscreen 静态不发（运行时状态，静态发即对上游 CSS 说谎）。
        var insets = ChromeInsets.Resolve(heightPx, fullscreen: false);

        // 命令名一律从 HostCommandIds 生成（单一事实源）：页面脚本里的 JS 字面量复写会让改名静默失配。
        string closeCmd = AppJsonContext.JsString(HostCommandIds.WindowClose);
        string minimizeCmd = AppJsonContext.JsString(HostCommandIds.WindowMinimize);
        string toggleCmd = AppJsonContext.JsString(HostCommandIds.WindowToggleMaximize);
        string fullscreenCmd = AppJsonContext.JsString(HostCommandIds.WindowSetFullscreen);
        string dragCmd = AppJsonContext.JsString(HostCommandIds.WindowStartDrag);
        string resizeCmd = AppJsonContext.JsString(HostCommandIds.WindowStartResize);

        return "(function(){" +
               // 脚本可能被注入到子 frame（引擎侧求值不保证顶层）：只给顶层文档建壳顶栏，
               // 否则 dsh 的层级子文档会各长一条。
               "if(window.top!==window.self)return;" +
               // 同一文档只装一次监听：引导页（wwwroot/index.html）自带同 id 的静态条，旧守卫「遇 bar 即 return」
               // 会让该页的拖拽与描边环整段缺失——条与监听分开守卫。
               "if(window.__dsh_caption_ready)return;" +
               "window.__dsh_caption_ready=1;" +
               "var id='" + ElementId + "';" +
               "var bar=document.getElementById(id);" +
               // 样式独立于条存在性：引导页自带静态条时仍需要三变量/描边环等规则（静态条只带条本身）。
               "if(!document.querySelector(\"style[data-dsh-desktop='caption-bar']\")){" +
               "var st=document.createElement('style');" +
               "st.setAttribute('data-dsh-desktop','caption-bar');" +
               "st.textContent=" + AppJsonContext.JsString(BuildCss(insets, heightPx, greenAttr)) + ";" +
               "(document.head||document.documentElement).appendChild(st);}" +
               "if(!bar){" +
               "bar=document.createElement('div');" +
               "bar.id=id;" +
               "bar.innerHTML=" + AppJsonContext.JsString(BuildBarInnerHtml(greenAttr)) + ";" +
               "var L={zh:[" + AppJsonContext.JsString(closeZh) + "," + AppJsonContext.JsString(minimizeZh) + ","
                   + AppJsonContext.JsString(greenZh) + "]," +
               "en:[" + AppJsonContext.JsString(closeEn) + "," + AppJsonContext.JsString(minimizeEn) + ","
                   + AppJsonContext.JsString(greenEn) + "]};" +
               "var lg=document.documentElement.lang||'';" +
               "var names=(lg?(lg.toLowerCase().indexOf('en')===0):" + (english ? "true" : "false") + ")?L.en:L.zh;" +
               "bar.children[0].setAttribute('aria-label',names[0]);" +
               "bar.children[1].setAttribute('aria-label',names[1]);" +
               "bar.children[2].setAttribute('aria-label',names[2]);" +
               "(document.body||document.documentElement).appendChild(bar);}" +
               // 描边环独立守卫（id 与顶栏不同，存在性各判各的；见 EdgeRingId）。
               "if(!document.getElementById('" + EdgeRingId + "')){" +
               "var ring=document.createElement('div');ring.id='" + EdgeRingId + "';" +
               "(document.body||document.documentElement).appendChild(ring);}" +
               BuildButtonBindings(macChrome, closeCmd, minimizeCmd, toggleCmd, fullscreenCmd) +
               // 带上界取自 Core 模型（值恒等于 HeightPx，见 ChromeInsets）：渲染只消费，不重算。
               CaptionBarDragScript.BuildDrag(insets.TopClearancePx, edgeResize, dragCmd, resizeCmd, toggleCmd) +
               "})();";
    }

    /// <summary>注入样式的总装（只拼装不计算）：三变量发布 + 侧栏让位 + 折叠按钮归位 + 红绿灯几何
    /// + 窗口边缘描边环。不含 macOS 毛玻璃透明链：旧 Ryn 宿主的窗口侧 backdrop（<c>RynOptions.Backdrop</c>）
    /// 已随 G5 迁移下线，Avalonia 宿主窗口不透明、<c>NativeWebView</c> 包无背景透明旋钮——页侧单边
    /// 透明只会把侧栏洗成白底近似色（摘除见 ADR caption-bar-vibrancy-chain-removal）。拖拽判定另见
    /// <see cref="BuildDragBinding"/>（行为保持——删除主张已在订正中落败，见 ADR）。</summary>
    /// <param name="insets">三变量几何（由 Core <c>ChromeInsets.Resolve</c> 解出，本方法只渲染）。</param>
    /// <param name="heightPx">chrome 高度（CSS 像素）。</param>
    /// <param name="greenAttr">绿灯按钮属性名（绿底规则随之切换）。</param>
    /// <returns>整段 CSS 文本（由调用方经 JsString 注入）。</returns>
    private static string BuildCss(ChromeInsets insets, int heightPx, string greenAttr) =>
        BuildChromeVariables(insets) +
        BuildSidebarClearance(heightPx) +
        BuildToggleReturn() +
        BuildTrafficLights(greenAttr, heightPx) +
        BuildEdgeRing();

    /// <summary>三变量发布（语义归 <see cref="Core.WindowChrome.ChromeInsets"/>，本方法只渲染模型值、
    /// 不重算）：top-clearance 供客户端 JS 读数让布局/浮层定位避开；chrome-top 恒为 0（上游该变量
    /// 只为窗外原生 caption 留空，本壳顶带是窗内页面，随高度发布会把顶带排除在模态遮罩绘制之外）；
    /// overlay-top 让对话框/菜单避开顶带（上游 overlay-top 语义；本壳无全屏状态源，恒按非全屏发布）。
    /// 自带本壳自有变量供下方 calc 复用。</summary>
    /// <param name="insets">三变量几何（由 Core <c>ChromeInsets.Resolve</c> 解出）。</param>
    /// <returns><c>:root</c> 变量段 CSS 文本。</returns>
    private static string BuildChromeVariables(ChromeInsets insets) =>
        ":root{" + TopClearanceVar + ":" + insets.TopClearancePx + "px;" +
        ChromeTopVar + ":" + insets.ChromeTopPx + "px;" +
        OverlayTopVar + ":" + insets.OverlayTopPx + "px;" +
        DeskCaptionHVar + ":" + insets.TopClearancePx + "px}";

    /// <summary>侧栏列顶部让位：padding 在元素自身背景盒内，让位带由侧栏填充覆盖 → 无缝无带（macOS 观感）。
    /// 侧栏列无稳定的 data-* 钩子（真实 DOM 实测：frame > .&lt;hash&gt;_sidebarCol），故双选择器兜底：类名后缀
    /// （CSS Modules 原名后缀）+ 结构式。结构式用 :where() 归零权重、并要求父级至少有第二个孩子——
    /// 侧栏列一旦挪位，最坏只让本规则不再命中（顶区无让位），绝不把让位间距打到内容列首元素上（评审 S3）。</summary>
    /// <param name="heightPx">chrome 高度（CSS 像素）：让位高度。</param>
    /// <returns>侧栏让位段 CSS 文本。</returns>
    private static string BuildSidebarClearance(int heightPx) =>
        "[class*=\"sidebarCol\"]," +
        ":where(:has(> [data-shell-bottom]):has(> :nth-child(2)) > :first-child){" +
        "padding-top:" + heightPx + "px!important}";

    /// <summary>折叠按钮回到让位带右端（官方 darwin 布局把它放顶条里、与红绿灯同行）：先用 transform 上移
    /// （不改布局盒，x 与侧栏宽度无关），但品牌行自带 overflow:hidden 会把它裁掉——故同批放开该行
    /// 裁剪（实测：不放开则按钮几何到位却画不出来）；折叠态把裁剪与位移一并还原，按钮留在轨内可点。
    /// 不新增自绘按钮（会与原生重复，实测踩过）。</summary>
    /// <returns>折叠按钮归位段 CSS 文本。</returns>
    private static string BuildToggleReturn() =>
        "[class*=\"logoRow\"]{overflow:visible!important}" +
        "[class*=\"collapsed\"] [class*=\"logoRow\"]{overflow:hidden!important}" +
        "[class*=\"sidebarCol\"] button[class*=\"toggle\"]{" +
        "transform:translateY(calc(-1 * (var(" + DeskCaptionHVar + ") + 10px)))}" +
        "[class*=\"collapsed\"] button[class*=\"toggle\"]{transform:none}";

    /// <summary>红绿灯：12px 系统色圆点，位置对齐官方 trafficLightPosition(16,18)，悬停出深色符号。
    /// 官方 macOS/Windows 的窗口按钮都是宿主原生（AppKit / Chromium overlay），页面从不绘制——
    /// 自绘灯没有那层「网页之外」的保障，故两态都要自己安排：展开时贴官方位（灯下有侧栏填充），
    /// 折叠时侧栏收为窄轨，三点随之收进轨内（否则会漂到内容区上 = 显示在外面）。
    /// 不用自绘底衬：底衬在两态宽度不同会露出色块补丁，收进窄轨后灯下天然是侧栏/轨道填充色。
    /// 注入条<b>必须保持可命中</b>（不设 pointer-events:none）：三个圆点靠它承载点击。
    /// 顶带拖拽<b>不由本条的命中面积决定</b>——判定已收归本壳（见 BuildDragBinding）：dsh 把顶带
    /// 留给自顶带起算的流内 chrome（列容器与会话头部），「盒底 ≤ 条带上界」式判据对「盖住条带却向下
    /// 延伸」的元素一律按内容处理；注入条只是碰巧落在带内的一个小命中体，
    /// 不再是拖拽面的唯一来源（条内非按钮区域仍算壳带，见判据的 fixed 例外）。</summary>
    /// <param name="greenAttr">绿灯按钮属性名（绿底规则随之切换）。</param>
    /// <param name="heightPx">chrome 高度（CSS 像素）：注入条高度。</param>
    /// <returns>红绿灯段 CSS 文本。</returns>
    private static string BuildTrafficLights(string greenAttr, int heightPx) =>
        "#" + ElementId + "{position:fixed;top:0;left:0;height:" + heightPx + "px;display:flex;" +
        "align-items:flex-start;gap:8px;padding:18px 0 0 16px;z-index:2147483647}" +
        "#" + ElementId + " button{width:12px;height:12px;padding:0;border-radius:50%;" +
        "position:relative;display:flex;align-items:center;justify-content:center;" +
        "border:1px solid rgba(0,0,0,.12);cursor:default}" +
        "#" + ElementId + " button[data-webview-close]{background:#ff5f57}" +
        "#" + ElementId + " button[data-webview-minimize]{background:#febc2e}" +
        "#" + ElementId + " button[" + greenAttr + "]{background:#28c840}" +
        "#" + ElementId + " button svg{display:none;width:6px;height:6px;color:rgba(0,0,0,.55)}" +
        "#" + ElementId + " button:hover svg{display:block}" +
        // 折叠态（侧栏收窄轨）：三点收进轨宽（约 56px）内——内缩 10 + 3×10 + 2×6 = 52，落在轨的填充上
        "html:has([class*=\"sidebarCol\"] [class*=\"collapsed\"]) #" + ElementId +
        "{padding:20px 0 0 10px;gap:6px}" +
        "html:has([class*=\"sidebarCol\"] [class*=\"collapsed\"]) #" + ElementId +
        " button{width:10px;height:10px}";

    /// <summary>窗口边缘描边环（ADR window-edge-ring）：无边框 + 不透明窗在合成器侧无阴影时（如 Linux
    /// Mutter），白内容落在白背景上即无边界可辨——与 ZCode 的透明窗 + 内层圆角不同，本壳不切透明窗，
    /// 只在视口内侧描 1px 发丝线。色标借 dsh 自有 <c>--dsw-alias-border-l3</c>（侧栏分割线同源，
    /// 明暗主题自动跟；缺失回退中灰——双主题都可见）。<c>pointer-events:none</c> 是承重要求：
    /// 环贴在视口四边，可命中即吞掉无边框窗口的边缘 resize 与边缘点击，故它是全脚本<b>唯一</b>允许
    /// 该声明的规则（注入条与 leading 座——如有——永远可命中）。<c>z-index</c> 紧贴顶栏之下、
    /// 浮于模态遮罩之上：窗口边缘任何时候可见。</summary>
    /// <returns>描边环段 CSS 文本。</returns>
    private static string BuildEdgeRing() =>
        "#" + EdgeRingId + "{position:fixed;inset:0;pointer-events:none;" +
        "box-shadow:inset 0 0 0 1px var(--dsw-alias-border-l3,rgba(127,127,127,.4));" +
        "z-index:2147483646}";

    /// <summary>双击自判的时间窗（毫秒）：本壳起拖的原生移动抓取使 DOM 的 <c>dblclick</c> 在顶带上不可达
    /// （见 <see cref="BuildDragBinding"/>），故按两次带内左键按下的间隔自判。取各平台双击窗的<b>上界</b>
    /// （Windows/macOS 默认 500ms；GTK 默认 400ms）：宽出的唯一效果是两次快速独立按下被当作双击——
    /// 正是原生标题栏语义；窄了则慢双击丢缩放（兜底路在顶带上不可达）。</summary>
    internal const int DoubleClickMs = 500;

    /// <summary>双击自判的位移窗（CSS 像素）：两次按下的点位漂移超过它即视为两次独立起拖。</summary>
    internal const int DoubleClickSlopPx = 4;

    /// <summary>边缘缩放命中宽度（CSS 像素）：无边框窗口的边缘缩放由页面判定（宿主按边发平台消息）。</summary>
    internal const int ResizeEdgePx = 6;

    /// <summary>红绿灯三点标记：关闭/最小化走窗口命令属性，绿灯属性随平台给（见 <see cref="MacGreenAttr"/>）。</summary>
    /// <param name="greenAttr">绿灯按钮属性名。</param>
    /// <returns>注入条 innerHTML（由调用方经 JsString 注入）。</returns>
    private static string BuildBarInnerHtml(string greenAttr) =>
        "<button data-webview-close>" + IconClose + "</button>" +
        "<button data-webview-minimize>" + IconMinimize + "</button>" +
        "<button " + greenAttr + ">" + IconMaximize + "</button>";

    /// <summary>macOS 绿灯的原生全屏点击绑定：直接绑在绿灯元素上（随元素创建/销毁，不引入全局标志与
    /// 选择器，也就不存在「元素被移除后重复注入叠监听」的缝；元素幂等守卫已保证每文档只建一次）。
    /// 判据是无状态几何现算：壳不提供 fullscreen 查询命令，故以 |innerHeight-screen.height|≤2 且
    /// |innerWidth-screen.width|≤2 判为已全屏（全屏铺满整屏且菜单栏自动隐藏；窗口缩放只占工作区、
    /// 高度差 &gt; 2），每次点击现算——导航与外部退出都不会让状态漂移。已知盲区：系统开着「自动隐藏
    /// 菜单栏」且 Dock 也自动隐藏时，缩放窗也铺满屏框 → 被读成全屏、首击退化为 no-op（⌃⌘F 仍可用）；
    /// 此面列入 ADR 的 macOS 真机验收项。</summary>
    private static string BuildButtonBindings(
        bool macChrome,
        string closeCmd,
        string minimizeCmd,
        string toggleCmd,
        string fullscreenCmd)
    {
        string fullscreen = "bar.children[2].onclick=function(){var fs=false;" +
            "try{fs=Math.abs(window.innerHeight-window.screen.height)<=2&&" +
            "Math.abs(window.innerWidth-window.screen.width)<=2;}catch(err){/* no window.screen in old engines: treat as not fullscreen */}" +
            "call(" + fullscreenCmd + ",{fullscreen:!fs});};";

        // 三键各自 onclick（重复注入时覆盖而非叠监听）。
        string green = macChrome ? fullscreen : "bar.children[2].onclick=function(){call(" + toggleCmd + ");};";
        return "bar.children[0].onclick=function(){call(" + closeCmd + ");};" +
               "bar.children[1].onclick=function(){call(" + minimizeCmd + ");};" + green;
    }

    /// <summary>关闭符号：红绿灯悬停 ×（悬停才显示，叠在圆点上）。</summary>
    private const string IconClose = "<svg viewBox='0 0 6 6'><path d='M0.5 0.5l5 5M5.5 0.5l-5 5' stroke='currentColor' stroke-width='1' fill='none'/></svg>";

    /// <summary>最小化符号：红绿灯悬停 −。</summary>
    private const string IconMinimize = "<svg viewBox='0 0 6 6'><path d='M0.5 3h5' stroke='currentColor' stroke-width='1' fill='none'/></svg>";

    /// <summary>绿灯符号：悬停对角展开箭头（点击语义＝窗口缩放 toggleMaximize；macOS 下为原生全屏，图标同形）。</summary>
    private const string IconMaximize = "<svg viewBox='0 0 6 6'><path d='M3.8 0.5h1.7v1.7M2.2 5.5H0.5V3.8M5.5 0.5L3.5 2.5M0.5 5.5l2-2' stroke='currentColor' stroke-width='1' fill='none'/></svg>";
}
