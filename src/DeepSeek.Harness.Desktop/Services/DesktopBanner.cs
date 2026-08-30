namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主横幅注入脚本的单一工厂：宿主主动注入的顶部横幅（版本底线 / 非受控退出 / 更新就绪）
/// 共用同一 DOM 结构与堆叠规则，消除多处手拼脚本的镜像漂移。
/// </summary>
/// <remarks>
/// 堆叠偏移在注入时按「已存在的已知横幅数量」运行时计算（每张 44px）——
/// 多横幅同现时依次下移，不再互相压叠；新增横幅只需登记 id 并给配色，无需改动其他横幅的守卫链。
/// </remarks>
public static class DesktopBanner
{
    /// <summary>全部宿主横幅 id（堆叠计数依据；新增横幅必须登记在此）。</summary>
    public static readonly string[] KnownIds =
    {
        "dsh-desktop-version-floor-banner",
        "dsh-desktop-run-marker-banner",
        "dsh-desktop-update-ready-banner",
    };

    /// <summary>横幅配色组（底色 / 前景 / 分隔线 / 按钮底色）。</summary>
    public sealed record Palette(string Background, string Foreground, string Border, string Button);

    /// <summary>生成横幅注入脚本（纯函数可单测）：幂等 id 守卫 + 运行时堆叠偏移。</summary>
    /// <param name="okLabel">确认按钮文案（宿主按注入时刻 locale 选择，ADR host-ui-locale）。</param>
    public static string Build(string id, string text, Palette palette, string okLabel = "知道了")
    {
        string known = string.Join(",", KnownIds.Select(k => "'" + k + "'"));
        return "(function(){" +
               "var id='" + id + "';" +
               "if(document.getElementById(id))return;" +
               "var known=[" + known + "];" +
               "var n=0;" +
               "for(var i=0;i<known.length;i++)if(document.getElementById(known[i]))n++;" +
               "var b=document.createElement('div');" +
               "b.id=id;" +
               "b.style.cssText='position:fixed;top:'+(n*44)+'px;left:0;right:0;z-index:2147483647;display:flex;gap:12px;align-items:center;justify-content:center;padding:8px 16px 8px 40px;background:" + palette.Background + ";color:" + palette.Foreground + ";font:13px/1.5 system-ui,sans-serif;border-bottom:1px solid " + palette.Border + "';" +
               "b.textContent=" + AppJsonContext.JsString(text) + ";" +
               "var x=document.createElement('button');" +
               "x.textContent=" + AppJsonContext.JsString(okLabel) + ";" +
               "x.style.cssText='flex:none;padding:2px 10px;background:" + palette.Button + ";color:#fff;border:0;border-radius:6px;cursor:pointer;font-size:12px';" +
               "x.onclick=function(){b.remove()};" +
               "b.appendChild(x);" +
               "(document.body||document.documentElement).appendChild(b);" +
               "})();";
    }
}
