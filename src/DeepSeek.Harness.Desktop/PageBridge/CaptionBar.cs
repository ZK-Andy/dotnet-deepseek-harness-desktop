namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 无边框窗口 chrome 注入脚本的单一工厂：三端统一官方 macOS 形态——内容满幅到顶、无 caption 色带，
/// 靠给页面设置 <c>html[data-platform='darwin']</c> 激活 dsh web 端内建的桌面呈现（侧栏 52px 顶条、
/// 折叠全隐、chrome 行布局，ADR frameless-uniform-caption-bar）。窗口控制三端同为注入的红绿灯三点
/// （12px 系统色圆点悬浮于侧栏顶条）；不用 Ryn Overlay 原生红绿灯——mac x64（Rosetta）在 macOS 26
/// 上窗口创建后 segfault（release run 37532237576 两轮同签名，arm64 同批全绿）。
/// </summary>
/// <remarks>
/// 按钮不带任何事件处理器——点击语义全部走 Ryn 注入脚本对 <c>data-webview-*</c> 属性的委托监听
/// （<c>window.minimize/toggleMaximize/close</c> IPC，能力面见 ryn.json 的 <c>window</c> 节）。
/// 本工厂不画任何背景面——侧栏顶条由 dsh web 自己画（配色天然一致，不存在色带错位）。
/// dsh 的 darwin 呈现含「html/body 透明 + 侧栏半透 tint」的毛玻璃链；无 vibrancy 的普通窗口上
/// 该链会让侧栏透到 webview 底色，故 mark 同时把 body 底色压回 <c>--dsw-alias-bg-base</c>
/// （扁平化，与浏览器渲染一致；vibrancy 留作后续）。
/// </remarks>
internal static class CaptionBar
{
    /// <summary>红绿灯簇元素 id：幂等守卫依据；wwwroot/index.html 静态簇使用同一 id
    /// （先渲染者胜，注入脚本遇之即让位）。</summary>
    public const string ElementId = "dsh-desktop-caption-bar";

    /// <summary>生成红绿灯三点注入脚本（三端统一，纯函数可单测）：darwin 标记 + 透明链扁平化
    /// （dsh web 内建 macOS 呈现的开关；darwin 规则下 html/body 透明、侧栏半透 tint，无 vibrancy
    /// 窗口不扁平化即暗色主题侧栏洗白，style 带查重守卫防双路注入重复追加）+ 幂等 id 守卫 +
    /// 三按钮声明式属性。可达名（aria-label）经 <see cref="AppJsonContext.JsString"/> 管线转义注入，
    /// 随宿主 locale 取值。innerHTML 全段走 JsString 管线（SVG 单引号裸拼会炸解析——评审 B1）。</summary>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文，ADR host-ui-locale）。</param>
    public static string Build(UiLocale? uiLocale = null)
    {
        (string minimize, string maximize, string close) = UiCopy.CaptionButtonNames(uiLocale?.IsEnglish == true);
        // 官方 macOS 红绿灯规格：12px 圆点、约 20px 间距、悬浮于侧栏顶条（无背景面）；
        // 悬停出深色符号（× − 缩放），点色 = macOS 系统色。
        string css =
            "html[data-platform='darwin'] body{background:var(--dsw-alias-bg-base,#fff)!important}" +
            "#" + ElementId + "{position:fixed;top:0;left:0;z-index:2147483647;display:flex;" +
            "padding:14px 0 0 12px;pointer-events:none}" +
            "#" + ElementId + " button{width:20px;height:20px;border:0;padding:0;background:transparent;" +
            "display:flex;align-items:center;justify-content:center;position:relative;" +
            "pointer-events:auto;cursor:default}" +
            "#" + ElementId + " button span{width:12px;height:12px;border-radius:50%;display:block;" +
            "border:1px solid rgba(0,0,0,.12)}" +
            "#" + ElementId + " button[data-webview-close] span{background:#ff5f57}" +
            "#" + ElementId + " button[data-webview-minimize] span{background:#febc2e}" +
            "#" + ElementId + " button[data-webview-maximize] span{background:#28c840}" +
            "#" + ElementId + " button svg{display:none;position:absolute;width:8px;height:8px;" +
            "color:rgba(0,0,0,.55)}" +
            "#" + ElementId + " button:hover svg{display:block}";
        string barInnerHtml =
            "<button data-webview-close>" + IconClose + "<span></span></button>" +
            "<button data-webview-minimize>" + IconMinimize + "<span></span></button>" +
            "<button data-webview-maximize>" + IconMaximize + "<span></span></button>";
        return "(function(){" +
               "document.documentElement.setAttribute('data-platform','darwin');" +
               "var id='" + ElementId + "';" +
               "if(document.getElementById(id))return;" +
               "if(document.querySelector(\"style[data-dsh-desktop='caption-mark']\"))return;" +
               "var st=document.createElement('style');" +
               "st.setAttribute('data-dsh-desktop','caption-mark');" +
               "st.textContent=" + AppJsonContext.JsString(css) + ";" +
               "(document.head||document.documentElement).appendChild(st);" +
               "var bar=document.createElement('div');" +
               "bar.id=id;" +
               "bar.innerHTML=" + AppJsonContext.JsString(barInnerHtml) + ";" +
               "bar.children[0].setAttribute('aria-label'," + AppJsonContext.JsString(close) + ");" +
               "bar.children[1].setAttribute('aria-label'," + AppJsonContext.JsString(minimize) + ");" +
               "bar.children[2].setAttribute('aria-label'," + AppJsonContext.JsString(maximize) + ");" +
               "(document.body||document.documentElement).appendChild(bar);" +
               "})();";
    }

    /// <summary>关闭符号：macOS 红绿灯悬停 ×（悬停才显示，叠在圆点上）。</summary>
    private const string IconClose = "<svg viewBox='0 0 8 8'><path d='M0.5 0.5l7 7M7.5 0.5l-7 7' stroke='currentColor' stroke-width='1.2' fill='none'/></svg>";

    /// <summary>最小化符号：macOS 红绿灯悬停 −。</summary>
    private const string IconMinimize = "<svg viewBox='0 0 8 8'><path d='M0.5 4h7' stroke='currentColor' stroke-width='1.2' fill='none'/></svg>";

    /// <summary>最大化符号：macOS 红绿灯悬停对角展开箭头（点击语义为窗口缩放 toggleMaximize）。</summary>
    private const string IconMaximize = "<svg viewBox='0 0 8 8'><path d='M5.5 0.5h2v2M2.5 7.5h-2v-2M7.5 0.5L4.5 3.5M0.5 7.5l3-3' stroke='currentColor' stroke-width='1.2' fill='none'/></svg>";
}
