namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 自绘标题栏（caption bar）注入脚本的单一工厂：三端统一无边框（<c>TitleBarStyle.Frameless</c>）后，
/// 窗口控制（最小化/最大化/关闭）与拖拽由注入页面的顶栏承担，样式对齐官方桌面端 caption 规格
/// （ADR frameless-uniform-caption-bar）。
/// </summary>
/// <remarks>
/// 按钮不带任何事件处理器——点击语义全部走 Ryn 注入脚本对 <c>data-webview-*</c> 属性的委托监听
/// （<c>window.minimize/toggleMaximize/close</c> IPC，能力面见 ryn.json 的 <c>window</c> 节）。
/// 内容让位：dsh 页面布局链是 <c>html/body/#root{height:100%}</c> 百分比链，<c>body{padding-top}</c> +
/// <c>box-sizing:border-box</c> 即整链干净下移，无底部剪裁。配色主路消费 dsh 主题 token（随页面明暗），
/// 回退值 = 官方规格（亮 <c>#f9fafb/#0f1115</c>，暗 <c>#1b1b1c/#f9fafb</c>，经 prefers-color-scheme）。
/// </remarks>
internal static class CaptionBar
{
    /// <summary>顶栏元素 id：幂等守卫与横幅堆叠偏移（<see cref="DesktopBanner"/>）的共同依据；
    /// wwwroot/index.html 静态条使用同一 id（先渲染者胜，注入脚本遇之即让位）。</summary>
    public const string ElementId = "dsh-desktop-caption-bar";

    /// <summary>生成顶栏注入脚本（纯函数可单测）：幂等 id 守卫 + 布局让位 + 三按钮声明式属性。
    /// 样式与可达名（aria-label）均为构建期已知值，经 <see cref="AppJsonContext.JsString"/> 管线转义注入，
    /// 随宿主 locale 取值。</summary>
    /// <param name="heightPx">顶栏高度（CSS 像素，<see cref="CaptionBarOptions.HeightPx"/>）。</param>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文，ADR host-ui-locale）。</param>
    public static string Build(int heightPx, UiLocale? uiLocale = null)
    {
        (string minimize, string maximize, string close) = UiCopy.CaptionButtonNames(uiLocale?.IsEnglish == true);
        string css =
            "#" + ElementId + "{--cap-fb-bg:#f9fafb;--cap-fb-fg:#0f1115}" +
            "@media(prefers-color-scheme:dark){#" + ElementId + "{--cap-fb-bg:#1b1b1c;--cap-fb-fg:#f9fafb}}" +
            "#" + ElementId + "{position:fixed;top:0;left:0;right:0;height:" + heightPx + "px;" +
            "display:flex;justify-content:flex-end;align-items:stretch;z-index:2147483647;" +
            "background:var(--dsw-alias-bg-overlay,var(--cap-fb-bg));" +
            "color:var(--dsw-alias-label-primary,var(--cap-fb-fg))}" +
            "body{padding-top:" + heightPx + "px!important;box-sizing:border-box!important}" +
            "#" + ElementId + " button{width:46px;border:0;background:transparent;color:inherit;padding:0;" +
            "display:flex;align-items:center;justify-content:center}" +
            "#" + ElementId + " button:hover{background:rgba(127,127,127,.18)}" +
            "#" + ElementId + " button[data-webview-close]:hover{background:#e81123;color:#fff}" +
            "#" + ElementId + " svg{width:10px;height:10px;display:block}";
        // innerHTML 全段（按钮 + SVG 图标，图标属性带单引号）整体走 JsString 管线——裸拼进单引号
        // JS 字符串会在首个 SVG 属性引号处语法终止，整个 IIFE 解析即炸（评审 B1）
        string barInnerHtml =
            "<button data-webview-minimize>" + IconMinimize + "</button>" +
            "<button data-webview-maximize>" + IconMaximize + "</button>" +
            "<button data-webview-close>" + IconClose + "</button>";
        return "(function(){" +
               "var id='" + ElementId + "';" +
               "if(document.getElementById(id))return;" +
               "var st=document.createElement('style');" +
               "st.textContent=" + AppJsonContext.JsString(css) + ";" +
               "(document.head||document.documentElement).appendChild(st);" +
               "var bar=document.createElement('div');" +
               "bar.id=id;" +
               "bar.setAttribute('data-webview-drag','');" +
               "bar.innerHTML=" + AppJsonContext.JsString(barInnerHtml) + ";" +
               "bar.children[0].setAttribute('aria-label'," + AppJsonContext.JsString(minimize) + ");" +
               "bar.children[1].setAttribute('aria-label'," + AppJsonContext.JsString(maximize) + ");" +
               "bar.children[2].setAttribute('aria-label'," + AppJsonContext.JsString(close) + ");" +
               "(document.body||document.documentElement).appendChild(bar);" +
               "})();";
    }

    /// <summary>最小化符号：Windows caption 同款中部横线（细线，随 currentColor）。</summary>
    private const string IconMinimize = "<svg viewBox='0 0 10 10'><path d='M0 5h10' stroke='currentColor' stroke-width='1'/></svg>";

    /// <summary>最大化符号：Windows caption 同款方框描边（静态方框，最大化态不区分还原符号——见 ADR Deferred）。</summary>
    private const string IconMaximize = "<svg viewBox='0 0 10 10'><rect x='0.5' y='0.5' width='9' height='9' fill='none' stroke='currentColor' stroke-width='1'/></svg>";

    /// <summary>关闭符号：Windows caption 同款对角叉线。</summary>
    private const string IconClose = "<svg viewBox='0 0 10 10'><path d='M0 0l10 10M10 0L0 10' stroke='currentColor' stroke-width='1'/></svg>";
}
