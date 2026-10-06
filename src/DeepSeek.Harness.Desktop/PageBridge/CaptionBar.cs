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
/// <c>window.dshDesktop</c> 假桥——本工厂只做宿主本职：量自己的 chrome、画自己的按钮。</para>
/// <para>按钮不带事件处理器——点击语义全部走 Ryn 注入脚本对 <c>data-webview-*</c> 属性的委托监听
/// （<c>window.minimize/toggleMaximize/close</c> IPC，能力面见 ryn.json 的 <c>window</c> 节）。</para>
/// </remarks>
internal static class CaptionBar
{
    /// <summary>红绿灯簇元素 id：幂等守卫依据；wwwroot/index.html 静态簇使用同一 id
    /// （先渲染者胜，注入脚本遇之即让位）。</summary>
    public const string ElementId = "dsh-desktop-caption-bar";

    /// <summary>生成启动注入脚本（三端同款，纯函数可单测）：宿主 chrome 高度变量 + 侧栏顶部让位 +
    /// 幂等守卫 + 红绿灯三按钮。可达名（aria-label）经 <see cref="AppJsonContext.JsString"/> 管线转义注入，
    /// 随宿主 locale 取值；innerHTML 全段同样走 JsString（SVG 单引号裸拼会炸解析——FULL 评审 B1）。</summary>
    /// <param name="heightPx">chrome 高度（CSS 像素，<see cref="Infrastructure.Runtime.CaptionBarOptions.HeightPx"/>；
    /// 与 Ryn 自动拖拽条同源，默认 52 = 官方 macOS 侧栏顶条）。</param>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文，ADR host-ui-locale）。</param>
    public static string Build(int heightPx, UiLocale? uiLocale = null)
    {
        (string minimize, string maximize, string close) = UiCopy.CaptionButtonNames(uiLocale?.IsEnglish == true);
        // ① 宿主 chrome 高度登进 dsh 公开变量（客户端 JS 读 top-clearance 让浮层避开；chrome-top 供遮罩），
        //    并自带 --dsh-desk-caption-h 供下方 calc 复用。
        // ② 侧栏列顶部让位：padding 在元素自身背景盒内，让位带由侧栏填充覆盖 → 无缝无带（macOS 观感）。
        //    侧栏列无稳定的 data-* 钩子（真实 DOM 实测：frame > .<hash>_sidebarCol），故双选择器兜底：
        //    类名后缀（CSS Modules 原名后缀）+ :has(frame 子标记) 结构式。
        // ③ 折叠按钮回到让位带右端（官方 darwin 布局把它放顶条里、与红绿灯同行）：先用 transform 上移
        //    （不改布局盒，x 与侧栏宽度无关），但品牌行自带 overflow:hidden 会把它裁掉——故同批放开该行
        //    裁剪（实测：不放开则按钮几何到位却画不出来）；折叠态把裁剪与位移一并还原，按钮留在轨内可点。
        //    不新增自绘按钮（会与原生重复，实测踩过）。
        // ④ 红绿灯：12px 系统色圆点，位置对齐官方 trafficLightPosition(16,18)，悬停出深色符号。
        //    官方 macOS/Windows 的窗口按钮都是宿主原生（AppKit / Chromium overlay），页面从不绘制——
        //    自绘灯没有那层「网页之外」的保障，故两态都要自己安排：展开时贴官方位（灯下有侧栏填充），
        //    折叠时侧栏收为窄轨，三点随之收进轨内（否则会漂到内容区上 = 显示在外面）。
        //    不用自绘底衬：底衬在两态宽度不同会露出色块补丁，收进窄轨后灯下天然是侧栏/轨道填充色。
        string css =
            ":root{--dsh-frame-top-clearance:" + heightPx + "px;--dsh-frame-chrome-top:" + heightPx + "px;" +
            "--dsh-desk-caption-h:" + heightPx + "px}" +
            "[class*=\"sidebarCol\"],:has(> [data-shell-bottom]) > :first-child{padding-top:" + heightPx + "px!important}" +
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
            "#" + ElementId + " button[data-webview-maximize]{background:#28c840}" +
            "#" + ElementId + " button svg{display:none;width:6px;height:6px;color:rgba(0,0,0,.55)}" +
            "#" + ElementId + " button:hover svg{display:block}" +
            // 折叠态（侧栏收窄轨）：三点收进轨宽（约 56px）内——内缩 10 + 3×10 + 2×6 = 52，落在轨的填充上
            "html:has([class*=\"sidebarCol\"] [class*=\"collapsed\"]) #" + ElementId +
            "{padding:20px 0 0 10px;gap:6px}" +
            "html:has([class*=\"sidebarCol\"] [class*=\"collapsed\"]) #" + ElementId +
            " button{width:10px;height:10px}";
        string barInnerHtml =
            "<button data-webview-close>" + IconClose + "</button>" +
            "<button data-webview-minimize>" + IconMinimize + "</button>" +
            "<button data-webview-maximize>" + IconMaximize + "</button>";
        return "(function(){" +
               "var id='" + ElementId + "';" +
               "if(document.getElementById(id))return;" +
               "if(!document.querySelector(\"style[data-dsh-desktop='caption-bar']\")){" +
               "var st=document.createElement('style');" +
               "st.setAttribute('data-dsh-desktop','caption-bar');" +
               "st.textContent=" + AppJsonContext.JsString(css) + ";" +
               "(document.head||document.documentElement).appendChild(st);}" +
               "var bar=document.createElement('div');" +
               "bar.id=id;" +
               "bar.innerHTML=" + AppJsonContext.JsString(barInnerHtml) + ";" +
               "bar.children[0].setAttribute('aria-label'," + AppJsonContext.JsString(close) + ");" +
               "bar.children[1].setAttribute('aria-label'," + AppJsonContext.JsString(minimize) + ");" +
               "bar.children[2].setAttribute('aria-label'," + AppJsonContext.JsString(maximize) + ");" +
               "(document.body||document.documentElement).appendChild(bar);" +
               "})();";
    }

    /// <summary>关闭符号：红绿灯悬停 ×（悬停才显示，叠在圆点上）。</summary>
    private const string IconClose = "<svg viewBox='0 0 6 6'><path d='M0.5 0.5l5 5M5.5 0.5l-5 5' stroke='currentColor' stroke-width='1' fill='none'/></svg>";

    /// <summary>最小化符号：红绿灯悬停 −。</summary>
    private const string IconMinimize = "<svg viewBox='0 0 6 6'><path d='M0.5 3h5' stroke='currentColor' stroke-width='1' fill='none'/></svg>";

    /// <summary>最大化符号：红绿灯悬停对角展开箭头（点击语义为窗口缩放 toggleMaximize）。</summary>
    private const string IconMaximize = "<svg viewBox='0 0 6 6'><path d='M3.8 0.5h1.7v1.7M2.2 5.5H0.5V3.8M5.5 0.5L3.5 2.5M0.5 5.5l2-2' stroke='currentColor' stroke-width='1' fill='none'/></svg>";
}
