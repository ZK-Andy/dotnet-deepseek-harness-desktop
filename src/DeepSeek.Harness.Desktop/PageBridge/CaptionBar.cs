namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 无边框窗口 chrome 注入脚本的单一工厂：官方 macOS 观感由<b>壳自己</b>实现——侧栏列顶部让位
/// （padding 属元素自身背景盒，让位带被侧栏填充色无缝覆盖）、左上角红绿灯三点（壳绘制，走 Ryn
/// 声明式 <c>data-webview-*</c> 控制）、并把宿主 chrome 高度登进 dsh 的两枚公开变量
/// （<c>--dsh-frame-top-clearance</c> 由客户端 JS 无条件读取、<c>--dsh-frame-chrome-top</c> 由浮层遮罩
/// 消费），使 dsh 自己的浮层避开本带（ADR frameless-uniform-caption-bar）。
/// </summary>
/// <remarks>
/// <para><b>禁止设置 <c>data-platform</c></b>（v0.6.1/0.6.2 启动失败根因）：该属性是 dsh 客户端的
/// <i>桌面运行时开关</i>——<c>detectEnvironment</c> 见之即判 <c>runtime="desktop"</c>（官方 Electron
/// 宿主语义），<c>dsh-client-shortcuts</c> 随即要求 <c>window.dshDesktop.keyboard</c>（官方 preload 桥）
/// 并 <c>throw</c>，级联 25 个 UI 插件 pending → 启动即插件加载失败屏。同理不设
/// <c>data-windows-titlebar</c>（那会切到 dsh 的 Windows 呈现，非本需求形态），也不注入任何
/// <c>window.dshDesktop</c> 假桥——本工厂只做宿主本职：量自己的 chrome、画自己的按钮。
/// 官方 macOS 的毛玻璃透明链同样以该属性门控，故本壳改在注入期按平台取舍（见 <see cref="BuildCss"/>）。</para>
/// <para>按钮通常不带事件处理器——点击语义走 Ryn 注入脚本对 <c>data-webview-*</c> 属性的委托监听
/// （<c>window.minimize/toggleMaximize/close</c> IPC，能力面见 ryn.json 的 <c>window</c> 节）；
/// 唯一例外是 macOS 绿灯：Ryn 无「原生全屏」属性，改由本工厂自绑点击调 <c>window.setFullscreen</c>。</para>
/// <para><b>注入时机三路互补</b>（ADR frameless-uniform-caption-bar「注入时机」条）：① 引擎侧
/// <c>IRynWebView.InjectScriptAsync</c> 注册「每次页面加载执行」的同一份脚本——三路里<b>唯一不靠导航
/// 事件覆盖后续每次加载</b>的一路（mac/win 的 <c>navigated</c> 挂在 URL/Source 变化上，holder 页
/// <c>location.reload()</c> 进真 UI 这类同 URL 重载不发；Linux 用户脚本同样逐文档重放，故该端 ③ 也覆盖）；
/// ② 接线时对当前文档立即注入一次（注册脚本只对<b>后续</b>文档生效）；③
/// <c>SetOnNavigatedPersistent</c> 导航到达后重挂（Linux 每跳都发、并带一次按当时 locale 重建）。
/// 幂等守卫保证三路叠加只建一条。</para>
/// <para><b>顶带拖拽判定收归本壳</b>（见 <see cref="BuildDragBinding"/>）：Ryn 的自动拖拽条在 dsh 的
/// 全高容器上从不触发，故本条带另挂自持判据，与 Ryn 判据互补。</para>
/// </remarks>
internal static class CaptionBar
{
    /// <summary>红绿灯簇元素 id：幂等守卫依据；wwwroot/index.html 静态簇使用同一 id
    /// （先渲染者胜，注入脚本遇之即让位）。</summary>
    public const string ElementId = "dsh-desktop-caption-bar";

    /// <summary>绿灯按钮属性（macOS）：本壳自有名——Ryn 注入脚本只识 <c>data-webview-maximize</c>
    /// （语义＝窗口缩放），原生全屏必须换名并自绑点击（语义见 <see cref="FullscreenClickBinding"/>）。</summary>
    private const string MacGreenAttr = "data-dsh-fullscreen";

    /// <summary>绿灯按钮属性（闸外，含非 macOS 与 Rosetta-x64）：Ryn 注入脚本识别的窗口缩放语义。</summary>
    private const string ZoomGreenAttr = "data-webview-maximize";

    /// <summary>生成注入脚本（除 macOS 两项差异外三端同款，纯函数可单测）：宿主 chrome 高度变量 +
    /// 侧栏顶部让位 + 顶层文档守卫 + 幂等守卫 + 红绿灯三按钮 + 带内拖拽判定。可达名（aria-label）双语
    /// 经 <see cref="AppJsonContext.JsString"/> 管线转义注入，执行期按 <c>html[lang]</c> 现取；
    /// innerHTML 全段同样走 JsString（SVG 单引号裸拼会炸解析——FULL 评审 B1）。
    /// macChrome 分支另挂原生全屏点击处理与毛玻璃透明链。三条注入路径共用本函数（见类注）。</summary>
    /// <param name="heightPx">chrome 高度（CSS 像素，<see cref="Infrastructure.Runtime.CaptionBarOptions.HeightPx"/>；
    /// 与 Ryn 自动拖拽条同源，默认 52 = 官方 macOS 侧栏顶条）。</param>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文，ADR host-ui-locale）：仅作 <c>html[lang]</c>
    /// 缺失时的回落值，文档自带语言时以文档为准。</param>
    /// <param name="macChrome">是否启用原生 macOS chrome（调用点传 <see cref="MacNativeChrome.IsEnabled"/>，
    /// 理由与闸界见其类注）：true 时绿灯走<i>原生全屏</i>而非窗口缩放，并追加毛玻璃透明链。否则恒 false。</param>
    public static string Build(int heightPx, UiLocale? uiLocale = null, bool macChrome = false)
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

        return "(function(){" +
               // 注册式执行是全 frame 注入（Ryn 的 inject 传 no_frames=false）：只给顶层文档建壳顶栏，
               // 否则 dsh 的层级子文档会各长一条。
               "if(window.top!==window.self)return;" +
               "var id='" + ElementId + "';" +
               "if(document.getElementById(id))return;" +
               "if(!document.querySelector(\"style[data-dsh-desktop='caption-bar']\")){" +
               "var st=document.createElement('style');" +
               "st.setAttribute('data-dsh-desktop','caption-bar');" +
               "st.textContent=" + AppJsonContext.JsString(BuildCss(heightPx, greenAttr, macChrome)) + ";" +
               "(document.head||document.documentElement).appendChild(st);}" +
               "var bar=document.createElement('div');" +
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
               "(document.body||document.documentElement).appendChild(bar);" +
               BuildDragBinding(heightPx) +
               (macChrome ? FullscreenClickBinding : "") +
               "})();";
    }

    /// <summary>注入样式：宿主 chrome 高度变量 + 侧栏让位 + 注入条与红绿灯几何（macChrome 追加透明链）。</summary>
    /// <param name="heightPx">chrome 高度（CSS 像素）。</param>
    /// <param name="greenAttr">绿灯按钮属性名（绿底规则随之切换）。</param>
    /// <param name="macChrome">是否追加 macOS 毛玻璃透明链。</param>
    /// <returns>整段 CSS 文本（由调用方经 JsString 注入）。</returns>
    private static string BuildCss(int heightPx, string greenAttr, bool macChrome)
    {
        // ① 宿主 chrome 高度登进 dsh 公开变量（客户端 JS 读 top-clearance 让浮层避开；chrome-top 供遮罩），
        //    并自带 --dsh-desk-caption-h 供下方 calc 复用。
        // ② 侧栏列顶部让位：padding 在元素自身背景盒内，让位带由侧栏填充覆盖 → 无缝无带（macOS 观感）。
        //    侧栏列无稳定的 data-* 钩子（真实 DOM 实测：frame > .<hash>_sidebarCol），故双选择器兜底：类名后缀
        //    （CSS Modules 原名后缀）+ 结构式。结构式用 :where() 归零权重、并要求父级至少有第二个孩子——
        //    侧栏列一旦挪位，最坏只让本规则不再命中（顶区无让位），绝不把让位间距打到内容列首元素上（评审 S3）。
        // ③ 折叠按钮回到让位带右端（官方 darwin 布局把它放顶条里、与红绿灯同行）：先用 transform 上移
        //    （不改布局盒，x 与侧栏宽度无关），但品牌行自带 overflow:hidden 会把它裁掉——故同批放开该行
        //    裁剪（实测：不放开则按钮几何到位却画不出来）；折叠态把裁剪与位移一并还原，按钮留在轨内可点。
        //    不新增自绘按钮（会与原生重复，实测踩过）。
        // ④ 红绿灯：12px 系统色圆点，位置对齐官方 trafficLightPosition(16,18)，悬停出深色符号。
        //    官方 macOS/Windows 的窗口按钮都是宿主原生（AppKit / Chromium overlay），页面从不绘制——
        //    自绘灯没有那层「网页之外」的保障，故两态都要自己安排：展开时贴官方位（灯下有侧栏填充），
        //    折叠时侧栏收为窄轨，三点随之收进轨内（否则会漂到内容区上 = 显示在外面）。
        //    不用自绘底衬：底衬在两态宽度不同会露出色块补丁，收进窄轨后灯下天然是侧栏/轨道填充色。
        //    注入条<b>必须保持可命中</b>（不设 pointer-events:none）：三个圆点靠它承载点击。
        //    顶带拖拽<b>不由本条的命中面积决定</b>——判定已收归本壳（见 BuildDragBinding）：dsh 把顶带
        //    留给全高容器，Ryn 的自动拖拽条对「盖住条带却向下延伸」的元素一律按内容处理，故那条带
        //    在 Ryn 侧从不触发；注入条只是碰巧落在带内的一个小命中体，不再是拖拽面的唯一来源。
        // ⑤ macOS 毛玻璃（官方 vibrancy 观感）：窗/网页背景已由 RynOptions.Backdrop 清成透明，页面侧补
        //    官方那条透明链——html/body 透明 + 侧栏列改半透明色调，NSVisualEffectView 经此透出。官方侧栏
        //    色标取 --dsw-specific-sidebar-fill 的渐变叠加，且整链以 html[data-platform='darwin'] 门控，
        //    而该属性正是本壳禁用的宿主冒充开关（见类注）；故改为注入期按平台收录本段、色标改用通用
        //    --dsw-alias-bg-base 的 80% 不透明 color-mix 近似（非 macOS 恒不含）。
        return ":root{--dsh-frame-top-clearance:" + heightPx + "px;--dsh-frame-chrome-top:" + heightPx + "px;" +
               "--dsh-desk-caption-h:" + heightPx + "px}" +
               "[class*=\"sidebarCol\"]," +
               ":where(:has(> [data-shell-bottom]):has(> :nth-child(2)) > :first-child){" +
               "padding-top:" + heightPx + "px!important}" +
               "[class*=\"logoRow\"]{overflow:visible!important}" +
               "[class*=\"collapsed\"] [class*=\"logoRow\"]{overflow:hidden!important}" +
               "[class*=\"sidebarCol\"] button[class*=\"toggle\"]{" +
               "transform:translateY(calc(-1 * (var(--dsh-desk-caption-h) + 10px)))}" +
               "[class*=\"collapsed\"] button[class*=\"toggle\"]{transform:none}" +
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
               " button{width:10px;height:10px}" +
               (macChrome
                   ? "html,body{background:transparent!important}[class*=\"sidebarCol\"]{" +
                     "background:color-mix(in srgb,var(--dsw-alias-bg-base,#fff) 80%,transparent)!important}"
                   : "");
    }

    /// <summary>带内拖拽判定（判定权收归本壳，ADR frameless-uniform-caption-bar）：Ryn 的自动拖拽条对
    /// 「盖住条带却向下延伸」的命中元素一律按内容处理（docs/custom-title-bars.md「overlays…content」），
    /// 而 dsh 把整条顶带留给<b>流内全高容器</b>（侧栏列/内容列的 padding 区）——Ryn 判据在我们这条带上
    /// 从不成立，顶带整条拖不动。此处补一条<b>互补</b>判据：命中元素是流内全高容器时才接管。
    /// <para>接管面（四条同时成立）：<c>clientY ≤ heightPx</c>；命中元素不是 html/body（那是 Ryn 的）；
    /// 不在交互元素内（表与 Ryn 的 INTERACTIVE 同款——语义元素照常点击）；命中元素<b>盒高 ≥ 0.9 视口高</b>
    /// 且<b>自身上溯无 fixed/absolute</b>（全高容器＝布局列；浮层/模态/侧栏分隔手柄都是定位元素 →
    /// 仍按内容处理，不吞事件）。</para>
    /// <para>与 Ryn 判据的关系与仲裁：Ryn 接管的点底边（`bottom`）≤ `strip×1.5`（非全高），本判据要求盒高 ≥ 0.9 视口高——
    /// 两者在正常窗高下<b>无交集</b>；唯一交集是「0.9 × 视口高 ≤ strip×1.5」的矮窗（约 &lt; 87px），
    /// 由两处仲裁闭合：<b>mousedown</b> 用 <c>defaultPrevented</c>（Ryn 的注入脚本在 CREATION 期先注册、
    /// 且先 <c>preventDefault()</c>），<b>dblclick</b> 用 <c>bandClaim</c>——本轮 <c>mousedown</c> 是否真的
    /// 由本壳接管（Ryn 的 dblclick 监听既不 <c>preventDefault</c> 也不可抢先，故对它
    /// <c>defaultPrevented</c> 无效；矮窗交集下正是 claim=false 关掉本壳的第二次 <c>toggleMaximize</c>）。
    /// <c>[data-webview-drag]</c> 命中一律让给 Ryn 的显式声明。</para>
    /// <para>最大化窗<b>不做</b>「先还原再拖」：GTK 的 <c>gdk_wayland_toplevel_begin_move</c> 无最大化守卫
    /// （只发 <c>xdg_toplevel.move</c> + 隐式抓取 serial），mutter 对最大化 surface 照常起抓取并按
    /// shake-loose 语义处置；壳先替它还原反而与原生标题栏行为相左（依据见 ADR 的 Alternatives）。</para>
    /// </summary>
    /// <param name="heightPx">chrome 高度（CSS 像素）：带内判据上界。</param>
    /// <returns>拖拽判定与两条 capture 监听（mousedown 起拖、dblclick 缩放）的 JS 段。</returns>
    private static string BuildDragBinding(int heightPx) =>
        // 交互元素表与 Ryn 注入脚本同款（RynWebView 的 INTERACTIVE）：本判据只接管「非交互」的点，
        // 语义控件（含本壳三个圆点、dsh 折叠按钮）照旧走各自点击语义。
        "var INTERACTIVE='button,a[href],input,select,textarea,summary,label," +
        "[contenteditable]:not([contenteditable=\"false\"]),audio[controls],video[controls]," +
        "[role=\"button\"],[role=\"link\"],[role=\"menuitem\"],[role=\"menuitemcheckbox\"]," +
        "[role=\"menuitemradio\"],[role=\"tab\"],[role=\"checkbox\"],[role=\"radio\"],[role=\"switch\"]," +
        "[role=\"slider\"],[role=\"combobox\"],[role=\"option\"],[role=\"textbox\"],[onclick]," +
        "[draggable=\"true\"],[data-webview-ignore],[data-webview-minimize],[data-webview-maximize]," +
        "[data-webview-close],[data-webview-resize]';" +
        "var bandClaim=false;" +
        "function bandPoint(e){" +
        "if(e.clientY>" + heightPx + ")return false;" +
        "var t=e.target;" +
        "if(!t||!t.closest)return false;" +
        "if(t===document.documentElement||t===document.body)return false;" +
        "if(t.closest(INTERACTIVE)||t.closest('[data-webview-drag]'))return false;" +
        "var r=t.getBoundingClientRect();" +
        "if(r.height<window.innerHeight*0.9)return false;" +
        "for(var n=t;n&&n!==document.documentElement;n=n.parentElement){" +
        "var p=window.getComputedStyle(n).position;" +
        "if(p==='fixed'||p==='absolute')return false;}" +
        "return true;}" +
        "document.addEventListener('mousedown',function(e){" +
        "bandClaim=false;" +
        "if(e.button!==0||e.defaultPrevented)return;" +
        "if(!bandPoint(e))return;" +
        "var w=window.__ryn;if(!w||!w.invoke)return;" +
        "e.preventDefault();" +
        "bandClaim=true;" +
        "var tb=window.__ryn_titlebar||{};" +
        "if(tb.mac)w.invoke('window.beginNativeDrag',{x:e.clientX,y:e.clientY});" +
        "else w.invoke('window.startDrag');" +
        "},true);" +
        "document.addEventListener('dblclick',function(e){" +
        // 双击另需 claim 仲裁：Ryn 的 dblclick 监听既不 preventDefault 也不可抢先（注册在先且无法观察其决定），
        // 故 defaultPrevented 对它无效——只有「本次点位上本壳 mousedown 真的接管了」才能证明双击归本壳。
        "if(!bandClaim||!bandPoint(e))return;" +
        "var w=window.__ryn;if(w&&w.invoke)w.invoke('window.toggleMaximize');" +
        "},true);";

    /// <summary>红绿灯三点标记：关闭/最小化走 Ryn 属性，绿灯属性随平台给（见 <see cref="MacGreenAttr"/>）。</summary>
    /// <param name="greenAttr">绿灯按钮属性名。</param>
    /// <returns>注入条 innerHTML（由调用方经 JsString 注入）。</returns>
    private static string BuildBarInnerHtml(string greenAttr) =>
        "<button data-webview-close>" + IconClose + "</button>" +
        "<button data-webview-minimize>" + IconMinimize + "</button>" +
        "<button " + greenAttr + ">" + IconMaximize + "</button>";

    /// <summary>macOS 绿灯的原生全屏点击绑定：直接绑在绿灯元素上（随元素创建/销毁，不引入全局标志与
    /// 选择器，也就不存在「元素被移除后重复注入叠监听」的缝；元素幂等守卫已保证每文档只建一次）。
    /// 判据是无状态几何现算：Ryn 0.38 无 fullscreen 查询命令，故以 |innerHeight-screen.height|≤2 且
    /// |innerWidth-screen.width|≤2 判为已全屏（全屏铺满整屏且菜单栏自动隐藏；窗口缩放只占工作区、
    /// 高度差 &gt; 2），每次点击现算——导航与外部退出都不会让状态漂移。已知盲区：系统开着「自动隐藏
    /// 菜单栏」且 Dock 也自动隐藏时，缩放窗也铺满屏框 → 被读成全屏、首击退化为 no-op（⌃⌘F 仍可用）；
    /// 此面列入 ADR 的 macOS 真机验收项。</summary>
    private const string FullscreenClickBinding =
        "bar.children[2].addEventListener('click',function(){" +
        "var fs=false;" +
        "try{fs=Math.abs(window.innerHeight-window.screen.height)<=2&&" +
        "Math.abs(window.innerWidth-window.screen.width)<=2;}" +
        "catch(err){/* no window.screen in old engines: treat as not fullscreen */}" +
        "if(window.__ryn&&window.__ryn.invoke)window.__ryn.invoke('window.setFullscreen',{fullscreen:!fs});" +
        "});";

    /// <summary>关闭符号：红绿灯悬停 ×（悬停才显示，叠在圆点上）。</summary>
    private const string IconClose = "<svg viewBox='0 0 6 6'><path d='M0.5 0.5l5 5M5.5 0.5l-5 5' stroke='currentColor' stroke-width='1' fill='none'/></svg>";

    /// <summary>最小化符号：红绿灯悬停 −。</summary>
    private const string IconMinimize = "<svg viewBox='0 0 6 6'><path d='M0.5 3h5' stroke='currentColor' stroke-width='1' fill='none'/></svg>";

    /// <summary>绿灯符号：悬停对角展开箭头（点击语义＝窗口缩放 toggleMaximize；macOS 下为原生全屏，图标同形）。</summary>
    private const string IconMaximize = "<svg viewBox='0 0 6 6'><path d='M3.8 0.5h1.7v1.7M2.2 5.5H0.5V3.8M5.5 0.5L3.5 2.5M0.5 5.5l2-2' stroke='currentColor' stroke-width='1' fill='none'/></svg>";
}
