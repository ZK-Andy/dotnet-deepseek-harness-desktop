namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// 自更新就绪横幅脚本（ADR shell-convenience-autostart-ready-notify）：ready 到达时一次性
/// 提示「新版本已就绪」。与既有横幅同款注入通道；幂等 id 守卫。
/// </summary>
public static class UpdateBanner
{
    /// <summary>生成 ready 横幅注入脚本（纯函数可单测）。</summary>
    public static string ReadyScript(string version)
    {
        var text = "新版本 " + version + " 已就绪，可在 设置 → 桌面设置 中一键安装。";
        return "(function(){" +
               "var id='dsh-desktop-update-ready-banner';" +
               "if(document.getElementById(id))return;" +
               "var b=document.createElement('div');" +
               "b.id=id;" +
               "b.style.cssText='position:fixed;top:0;left:0;right:0;z-index:2147483647;display:flex;gap:12px;align-items:center;justify-content:center;padding:8px 16px 8px 40px;background:#14251b;color:#d9f2e3;font:13px/1.5 system-ui,sans-serif;border-bottom:1px solid #1f3a2a';" +
               "if(document.getElementById('dsh-desktop-version-floor-banner')||document.getElementById('dsh-desktop-run-marker-banner'))b.style.top='44px';" +
               "b.textContent=" + JsString(text) + ";" +
               "var x=document.createElement('button');" +
               "x.textContent='知道了';" +
               "x.style.cssText='flex:none;padding:2px 10px;background:#2f855a;color:#fff;border:0;border-radius:6px;cursor:pointer;font-size:12px';" +
               "x.onclick=function(){b.remove()};" +
               "b.appendChild(x);" +
               "(document.body||document.documentElement).appendChild(b);" +
               "})();";
    }

    private static string JsString(string value) =>
        "\"" + System.Text.Json.JsonEncodedText.Encode(value).ToString() + "\"";
}
