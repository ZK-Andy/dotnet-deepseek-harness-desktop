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
/// </remarks>
internal static class CaptionBar
{
    /// <summary>红绿灯簇元素 id：幂等守卫依据；wwwroot/index.html 静态簇使用同一 id
    /// （先渲染者胜，注入脚本遇之即让位）。</summary>
    public const string ElementId = "dsh-desktop-caption-bar";

    /// <summary>绿灯按钮属性（macOS）：本壳自有名——Ryn 注入脚本只识 <c>data-webview-maximize</c>
    /// （语义＝窗口缩放），原生全屏必须换名并自绑点击（语义见 <see cref="FullscreenClickBinding"/>）。</summary>
    private const string MacGreenAttr = "data-dsh-fullscreen";

    /// <summary>绿灯按钮属性（非 macOS）：Ryn 注入脚本识别的窗口缩放语义。</summary>
    private const string ZoomGreenAttr = "data-webview-maximize";

    /// <summary>生成启动注入脚本（除 macOS 两项差异外三端同款，纯函数可单测）：宿主 chrome 高度变量 +
    /// 侧栏顶部让位 + 幂等守卫 + 红绿灯三按钮。可达名（aria-label）经 <see cref="AppJsonContext.JsString"/>
    /// 管线转义注入，随宿主 locale 取值；innerHTML 全段同样走 JsString（SVG 单引号裸拼会炸解析——FULL 评审 B1）。
    /// macChrome 分支另挂原生全屏点击处理与毛玻璃透明链。</summary>
    /// <param name="heightPx">chrome 高度（CSS 像素，<see cref="Infrastructure.Runtime.CaptionBarOptions.HeightPx"/>；
    /// 与 Ryn 自动拖拽条同源，默认 52 = 官方 macOS 侧栏顶条）。</param>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文，ADR host-ui-locale）。</param>
    /// <param name="macChrome">是否 macOS 原生语义（调用点传 <c>OperatingSystem.IsMacOS()</c>）：绿灯走
    /// <i>原生全屏</i>而非窗口缩放，并追加毛玻璃透明链（官方 vibrancy 观感）。非 macOS 恒 false——
    /// 其余平台的 backdrop 后端降级为 None，透明窗会直接露底。</param>
    public static string Build(int heightPx, UiLocale? uiLocale = null, bool macChrome = false)
    {
        bool english = uiLocale?.IsEnglish == true;
        (string minimize, string maximize, string close) = UiCopy.CaptionButtonNames(english);
        // 绿灯属性与可达名同源切换：绿底 CSS、innerHTML、aria 三处同源该变量（点击处理按第三子位置直绑，
        // 不取属性），避免「属性对、语义错」。
        string greenAttr = macChrome ? MacGreenAttr : ZoomGreenAttr;
        string greenLabel = macChrome ? UiCopy.CaptionFullscreenName(english) : maximize;

        return "(function(){" +
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
               "bar.children[0].setAttribute('aria-label'," + AppJsonContext.JsString(close) + ");" +
               "bar.children[1].setAttribute('aria-label'," + AppJsonContext.JsString(minimize) + ");" +
               "bar.children[2].setAttribute('aria-label'," + AppJsonContext.JsString(greenLabel) + ");" +
               "(document.body||document.documentElement).appendChild(bar);" +
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
        //    注入条<b>必须保持可命中</b>（不设 pointer-events:none）：Ryn 的自动拖拽条按 mousedown 的活体
        //    命中元素判定，盖住条带却向下延伸的元素一律按「内容」处理、不拖拽（Ryn docs/custom-title-bars.md
        //    「overlays…treated as content」）；本壳侧栏列的<b>元素盒</b>仍覆盖顶带（本壳只给它加 padding），
        //    盒高全窗 ⇒ 按内容处理，故这条自绘条（盒高 52 ≤ 阈值 78）是<b>侧栏让位区内</b>唯一可拖拽的
        //    命中体（dsh 中间列头部自身 ≤78px，那一带另有 dsh 自己的可拖面）。可拖面 = 条盒（≈68×52）
        //    扣掉三个 12×12 圆点后的余量。它盖住的是自家 padding 造出的空白侧栏区（该区无 dsh 控件），
        //    吞事件在此不损失可达性。
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
