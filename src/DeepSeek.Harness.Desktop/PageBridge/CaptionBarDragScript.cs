namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 顶带拖拽与边缘缩放的注入脚本（从 <see cref="CaptionBar"/> 拆出以守 F1 单文件 400 行）：
/// 只拼接字符串、不持状态；命令名与高度由调用方给出，本类不复写线协议字面量。
/// </summary>
internal static class CaptionBarDragScript
{
    /// <summary>带内拖拽判定（判定权收归本壳，ADR frameless-uniform-caption-bar）：命中元素属于顶带
    /// chrome 才接管——dsh 把整条顶带留给自顶带起算的流内 chrome（侧栏列的 padding 区、内容列的会话
    /// 头部），「盒底 ≤ 条带上界」式判据对「盖住条带却向下延伸」的元素一律按内容处理
    /// （上游 `docs/custom-title-bars.md`「overlays…content」），在我们这条带上不可靠。
    /// <para>接管面：<c>clientY ≤ heightPx</c>；命中元素不是 html/body；不在交互元素内
    /// （INTERACTIVE 表 + dsh 声明的 darwin no-drag 簇——顶带内的语义控件照常点击）；命中元素
    /// <b>盒顶在带内起算</b>（<c>rect.top ≤ heightPx</c>，按盒高判「全高」会漏掉条状会话头部）；自身上溯
    /// 无 fixed/absolute（浮层/模态/分隔手柄都是定位元素 → 仍按内容处理；本壳自绘条自身是 fixed，条内
    /// 非按钮区域仍算壳带）。</para>
    /// <para>边缘缩放（<c>edgeAt</c>）是窗口 chrome 语义，判定独立于顶带：6px 边缘内内容一律让位，
    /// 仅 <c>data-webview-ignore</c> 显式豁免；并随 pointermove 提供边缘缩放光标可供性
    /// （0.7.2 真机实证：按内容让位则 dsh 的侧栏/输入框/顶带盖满四边，缩放区永远打不中）。
    /// </para>
    /// <para>双击自判的机制与取值理由、以及「最大化窗不先还原再拖」的依据，单一事实源在 ADR
    /// （Decision「顶带拖拽判定收归本壳」条与 Alternatives）。</para>
    /// </summary>
    /// <param name="heightPx">chrome 高度（CSS 像素）：带内判据上界与盒顶判据上界。</param>
    /// <returns>拖拽判定与两条 capture 监听（mousedown 起拖/自判双击、dblclick 兜底）的 JS 段。</returns>
    public static string BuildDrag(
        int heightPx,
        bool edgeResize,
        string dragCmd,
        string resizeCmd,
        string toggleCmd)
    {
        const string invoke = "window.__shell";
        // 边缘缩放只在宿主真的提供缩放通道时启用：macOS 的边框缩放由窗口系统按 BorderOnly 给，
        // 页面侧再抢 6px 会既不缩放又吞点击。
        string edgeOn = edgeResize ? "true" : "false";
        return
        // 交互元素表（`data-webview-*` 委托监听的 INTERACTIVE 集），再并上 dsh 的 no-drag 簇（会话头部
        // 座位：官方在 darwin 下整座 no-drag，类名在所有平台都渲染）：本判据只接管「非交互」的点，
        // 语义控件照旧走各自点击语义。
        "var INTERACTIVE='button,a[href],input,select,textarea,summary,label," +
        "[contenteditable]:not([contenteditable=\"false\"]),audio[controls],video[controls]," +
        "[role=\"button\"],[role=\"link\"],[role=\"menuitem\"],[role=\"menuitemcheckbox\"]," +
        "[role=\"menuitemradio\"],[role=\"tab\"],[role=\"checkbox\"],[role=\"radio\"],[role=\"switch\"]," +
        "[role=\"slider\"],[role=\"combobox\"],[role=\"option\"],[role=\"textbox\"],[onclick]," +
        "[draggable=\"true\"],[data-webview-ignore],[data-webview-minimize],[data-webview-maximize]," +
        "[data-webview-close],[data-webview-resize]';" +
        "var NODRAG=INTERACTIVE+',[class*=\"headerLeading\"],[class*=\"headerActions\"]," +
        "[class*=\"headerUtilities\"],[class*=\"headerCorner\"]';" +
        "var bandClaim=false,lastDown=0,lastX=0,lastY=0;" +
        "var DBL_MS=" + CaptionBar.DoubleClickMs + ",DBL_SLOP=" + CaptionBar.DoubleClickSlopPx +
        ",EDGE_ON=" + edgeOn + ",EDGE=" + CaptionBar.ResizeEdgePx + ";" +
        "function call(cmd,args){try{var w=" + invoke + ";if(!w||!w.invoke)return;var p=w.invoke(cmd,args);if(p&&p.catch)p.catch(function(){});}catch(err){}}" +
        BuildEdgeScript(edgeResize) +
        "function bandPoint(e){" +
        "if(e.clientY>" + heightPx + ")return false;" +
        "var t=e.target;" +
        "if(!t||!t.closest)return false;" +
        "if(t===document.documentElement||t===document.body)return false;" +
        "if(t.closest(NODRAG)||t.closest('[data-webview-drag]'))return false;" +
        "if(t.getBoundingClientRect().top>" + heightPx + ")return false;" +
        "for(var n=t;n&&n!==document.documentElement;n=n.parentElement){" +
        "var p=window.getComputedStyle(n).position;" +
        "if(p==='fixed'||p==='absolute')return n.id===id;}" +
        "return true;}" +
        "document.addEventListener('mousedown',function(e){" +
        "bandClaim=false;" +
        "if(e.button!==0||e.defaultPrevented)return;" +
        // 边缘缩放先于顶带拖拽：角落同时属于两者时按缩放处理（原生标题栏同序）。
        "var ed=edgeAt(e);" +
        "if(ed){e.preventDefault();" +
        "call(" + resizeCmd + ",{edge:ed,x:window.screenX+e.clientX,y:window.screenY+e.clientY});return;}" +
        "if(!bandPoint(e))return;" +
        "var now=Date.now();" +
        "var dbl=now-lastDown<=DBL_MS&&Math.abs(e.clientX-lastX)<=DBL_SLOP&&" +
        "Math.abs(e.clientY-lastY)<=DBL_SLOP;" +
        "lastDown=dbl?0:now;lastX=e.clientX;lastY=e.clientY;" +
        "e.preventDefault();" +
        "if(dbl){call(" + toggleCmd + ");return;}" +
        "bandClaim=true;" +
        "call(" + dragCmd + ",{x:window.screenX+e.clientX,y:window.screenY+e.clientY});" +
        "},true);" +
        "document.addEventListener('dblclick',function(e){" +
        "if(!bandClaim||!bandPoint(e))return;" +
        "call(" + toggleCmd + ");" +
        "},true);";
    }

    /// <summary>边缘缩放脚本（<c>edgeAt</c> 判定、缩放光标映射与 pointermove 监听）：
    /// 边缘是窗口 chrome 语义——6px 边缘内内容一律让位（原生标题栏同规则），只有显式豁免
    /// （<c>data-webview-ignore</c>）可阻断；交互元素/NODRAG 簇不豁免——dsh 的侧栏/输入框/顶带
    /// 恰好盖满四边，按内容让位则边缘区永远打不中（0.7.2 真机实证：边缘缩放完全失效）。
    /// pointermove 光标监听随 <paramref name="edgeResize"/> 门控（macOS 走系统边框缩放，不上监听）。</summary>
    /// <param name="edgeResize">边缘缩放开关（宿主提供缩放通道才启用）。</param>
    /// <returns>边缘缩放 JS 段（<c>edgeAt</c>/<c>edgeCursor</c> 定义与可选 pointermove 监听）。</returns>
    private static string BuildEdgeScript(bool edgeResize)
    {
        return
        "function edgeAt(e){if(!EDGE_ON)return '';" +
        "var t=e.target;if(t&&t.closest&&t.closest('[data-webview-ignore]'))return '';" +
        "var w=window.innerWidth,h=window.innerHeight," +
        "x=e.clientX,y=e.clientY,t=y<=EDGE,b=y>=h-EDGE,l=x<=EDGE,r=x>=w-EDGE;" +
        "if(!(t||b||l||r))return '';" +
        "if(t&&l)return 'top-left';if(t&&r)return 'top-right';" +
        "if(b&&l)return 'bottom-left';if(b&&r)return 'bottom-right';" +
        "if(t)return 'top';if(b)return 'bottom';if(l)return 'left';return 'right';}" +
        "function edgeCursor(ed){if(!ed)return '';" +
        "if(ed==='left'||ed==='right')return 'ew-resize';" +
        "if(ed==='top'||ed==='bottom')return 'ns-resize';" +
        "return (ed==='top-left'||ed==='bottom-right')?'nwse-resize':'nesw-resize';}" +
        (edgeResize
            ? "document.addEventListener('pointermove',function(e){" +
              "document.documentElement.style.cursor=edgeCursor(edgeAt(e));" +
              "},true);"
            : "");
    }
}
