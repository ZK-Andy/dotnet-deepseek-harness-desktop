
namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>边缘缩放脚本判定（0.7.2 真机缺陷回归钉）：边缘是窗口 chrome 语义，交互元素/NODRAG
/// 簇不得把边缘区让位清空（dsh 侧栏/输入框/顶带盖满四边，按内容让位则缩放永远打不中）；并随
/// pointermove 提供缩放光标可供性。脚本为拼接字符串，断言钉关键语义片段防回潮。</summary>
public class CaptionBarDragScriptTests
{
    /// <summary>edgeAt 的排除面只留显式豁免（data-webview-ignore）：NODRAG/[data-webview-drag]
    /// 不得出现在边缘判定里。</summary>
    [Fact]
    public void BuildDrag_EdgeExclusion_KeepsOnlyExplicitIgnore()
    {
        string script = CaptionBarDragScript.BuildDrag(heightPx: 40, edgeResize: true, "wcmd", "rcmd", "tcmd");

        int edgeAt = script.IndexOf("function edgeAt", StringComparison.Ordinal);
        int cursor = script.IndexOf("function edgeCursor", StringComparison.Ordinal);
        Assert.True(edgeAt >= 0 && cursor > edgeAt, "edgeAt/edgeCursor 函数应存在且有序");
        string edgeBody = script[edgeAt..cursor];

        Assert.Contains("data-webview-ignore", edgeBody);
        Assert.DoesNotContain("closest(NODRAG)", edgeBody);
        Assert.DoesNotContain("[data-webview-drag]", edgeBody);
    }

    /// <summary>pointermove 光标可供性随 edgeResize 开关：开启含方向映射，关闭（macOS）整段缺席。</summary>
    [Fact]
    public void BuildDrag_CursorAffordance_FollowsEdgeResizeSwitch()
    {
        string on = CaptionBarDragScript.BuildDrag(40, edgeResize: true, "w", "r", "t");
        string off = CaptionBarDragScript.BuildDrag(40, edgeResize: false, "w", "r", "t");

        Assert.Contains("pointermove", on);
        Assert.Contains("ew-resize", on);
        Assert.Contains("ns-resize", on);
        Assert.Contains("nwse-resize", on);
        Assert.Contains("nesw-resize", on);
        Assert.DoesNotContain("pointermove", off);
    }

    /// <summary>边缘缩放关闭（macOS）时开关传 false：edgeAt 首行短路恒空。</summary>
    [Fact]
    public void BuildDrag_EdgeOff_ShortCircuitsEdgeAt()
    {
        string script = CaptionBarDragScript.BuildDrag(40, edgeResize: false, "w", "r", "t");

        Assert.Contains("EDGE_ON=false", script);
        Assert.Contains("if(!EDGE_ON)return ''", script);
    }
}
