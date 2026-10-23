namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 顶带拖拽与边缘缩放的注入脚本（从 <see cref="CaptionBar"/> 拆出以守 F1 单文件 400 行）：
/// 只拼接字符串、不持状态；命令名与高度由调用方给出，本类不复写线协议字面量。
/// </summary>
internal static class CaptionBarDragScript
{
    /// <summary>带内拖拽判定（判定权收归本壳，ADR frameless-uniform-caption-bar）：命中元素属于顶带
    /// chrome 才接管——dsh 把整条顶带留给自顶带起算的流内 chrome（侧栏列的 padding 区、内容列的会话
    /// 头部），Ryn 的自动拖拽条对「盖住条带却向下延伸」的命中元素一律按内容处理
    /// （docs/custom-title-bars.md「overlays…content」），在我们这条带上不可靠。
    /// <para>接管面：<c>clientY ≤ heightPx</c>；命中元素不是 html/body（那是 Ryn 的）；不在交互元素内
    /// （Ryn 的 INTERACTIVE 表 + dsh 声明的 darwin no-drag 簇——顶带内的语义控件照常点击）；命中元素
    /// <b>盒顶在带内起算</b>（<c>rect.top ≤ heightPx</c>，按盒高判「全高」会漏掉条状会话头部）；自身上溯
    /// 无 fixed/absolute（浮层/模态/分隔手柄都是定位元素 → 仍按内容处理；本壳自绘条自身是 fixed，条内
    /// 非按钮区域仍算壳带——本壳 chrome 由本壳负责，不依赖 Ryn 是否接管该点）。</para>
    /// <para>与 Ryn 判据的仲裁、双击自判的机制与取值理由、以及「最大化窗不先还原再拖」的依据，单一
    /// 事实源在 ADR（Decision「顶带拖拽判定收归本壳」条与 Alternatives）。</para>
    /// </summary>
    /// <param name="heightPx">chrome 高度（CSS 像素）：带内判据上界与盒顶判据上界。</param>
    /// <returns>拖拽判定与两条 capture 监听（mousedown 起拖/自判双击、dblclick 兜底）的 JS 段。</returns>
    public static string BuildDrag(
        int heightPx,
        CaptionBarChannel channel,
        bool edgeResize,
        string dragCmd,
        string resizeCmd,
        string toggleCmd)
    {
        string invoke = channel.InvokeGlobal;
        string shell = channel.ShellBridge ? "true" : "false";
        // 边缘缩放只在宿主真的提供缩放通道时启用：macOS 的边框缩放由窗口系统按 BorderOnly 给，
        // 页面侧再抢 6px 会既不缩放又吞点击。
        string edgeOn = edgeResize && channel.ShellBridge ? "true" : "false";
        return
        // 交互元素表与 Ryn 注入脚本同款（RynWebView 的 INTERACTIVE），再并上 dsh 的 no-drag 簇（会话头部
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
        "var DBL_MS=" + CaptionBar.DoubleClickMs + ",DBL_SLOP=" + CaptionBar.DoubleClickSlopPx + ",SHELL=" + shell +
        ",EDGE_ON=" + edgeOn + ",EDGE=" + CaptionBar.ResizeEdgePx + ";" +
        "function call(cmd,args){try{var w=" + invoke + ";if(!w||!w.invoke)return;var p=w.invoke(cmd,args);if(p&&p.catch)p.catch(function(){});}catch(err){}}" +
        "function edgeAt(e){if(!SHELL||!EDGE_ON)return '';" +
        "var t=e.target;if(t&&t.closest&&(t.closest(NODRAG)||t.closest('[data-webview-drag]')))return '';" +
        "var w=window.innerWidth,h=window.innerHeight," +
        "x=e.clientX,y=e.clientY,t=y<=EDGE,b=y>=h-EDGE,l=x<=EDGE,r=x>=w-EDGE;" +
        "if(!(t||b||l||r))return '';" +
        "if(t&&l)return 'top-left';if(t&&r)return 'top-right';" +
        "if(b&&l)return 'bottom-left';if(b&&r)return 'bottom-right';" +
        "if(t)return 'top';if(b)return 'bottom';if(l)return 'left';return 'right';}" +
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
        "if(SHELL){" +
        "call(" + dragCmd + ",{x:window.screenX+e.clientX,y:window.screenY+e.clientY});" +
        "return;}" +
        "var tb=" + channel.TitleBarGlobal + "||{};" +
        "if(tb.mac)call('window.beginNativeDrag',{x:e.clientX,y:e.clientY});" +
        "else call(" + dragCmd + ");" +
        "},true);" +
        "document.addEventListener('dblclick',function(e){" +
        "if(!bandClaim||!bandPoint(e))return;" +
        "call(" + toggleCmd + ");" +
        "},true);";
    }
}
