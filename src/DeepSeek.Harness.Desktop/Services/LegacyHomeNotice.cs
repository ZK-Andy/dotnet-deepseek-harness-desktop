using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 一次性只读旧 home 提示（ADR shared-home-desktop-profile）：检测 v0.2.x 私有 home 仍在时，
/// 日志 + 界面横幅告知新位置与旧数据路径。纯告知——不迁移、不改写、无持久标记；
/// 用户移走或删除旧目录后提示自然消失。回退通道 = 桌面专属覆盖环境变量
/// <see cref="HarnessRuntimeHost.HomeOverrideEnv"/> 指回旧目录。
/// </summary>
public static class LegacyHomeNotice
{
    /// <summary>v0.2.x 私有 home（历史默认值 <c>&lt;LocalApplicationData&gt;/DeepSeek.Harness.Desktop/dsh</c>）。</summary>
    public static string LegacyPrivateHome =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeek.Harness.Desktop",
            "dsh");

    /// <summary>旧私有 home 是否仍存在（只读探测）。</summary>
    public static bool IsPresent() => Directory.Exists(LegacyPrivateHome);

    /// <summary>
    /// 生成顶部横幅注入脚本（纯函数，可单测）。文案与路径整体经 JSON 序列化嵌入
    /// （Windows 反斜杠/引号安全）；幂等：同 id 已存在则跳过；附「知道了」关闭按钮。
    /// </summary>
    public static string BannerScript(string legacyHome, string newHome)
    {
        var text = "检测到旧版桌面数据目录 " + legacyHome +
                   "。新版改用共享数据目录 " + newHome +
                   "（与 CLI/Web 互通）；旧数据原地保留未迁移，可自行备份或删除，也可设 DSH_DESKTOP_DSH_HOME 指回旧目录。";
        return "(function(){" +
               "var id='dsh-desktop-legacy-home-banner';" +
               "if(document.getElementById(id))return;" +
               "var b=document.createElement('div');" +
               "b.id=id;" +
               "b.style.cssText='position:fixed;top:0;left:0;right:0;z-index:2147483647;display:flex;gap:12px;align-items:center;justify-content:center;padding:8px 16px 8px 40px;background:#1b1b26;color:#e6e6ea;font:13px/1.5 system-ui,sans-serif;border-bottom:1px solid #2a2a3a';" +
               "if(document.getElementById('dsh-desktop-version-floor-banner'))b.style.top='44px';" +
               "b.textContent=" + JsString(text) + ";" +
               "var x=document.createElement('button');" +
               "x.textContent='知道了';" +
               "x.style.cssText='flex:none;padding:2px 10px;background:#7c3aed;color:#fff;border:0;border-radius:6px;cursor:pointer;font-size:12px';" +
               "x.onclick=function(){b.remove()};" +
               "b.appendChild(x);" +
               "(document.body||document.documentElement).appendChild(b);" +
               "})();";
    }

    /// <summary>JS 字符串字面量（值经 JsonEncodedText 转义）：AOT 下避免反射序列化（UpdateState.ToJson 同款）。</summary>
    private static string JsString(string value) =>
        "\"" + JsonEncodedText.Encode(value).ToString() + "\"";
}
