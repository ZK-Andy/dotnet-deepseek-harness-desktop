using System.Text;
using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Recovery;

/// <summary>
/// 崩溃恢复页构建（纯函数可单测）：把「一行覆写」升级为内嵌静态恢复文档——失败原因、
/// 子进程 stderr 尾部展示、导出诊断与退出两个动作。恢复页出现的时刻 dsh 必然不可用，
/// 而按钮走的 <c>desktop.*</c> 命令是 Ryn 层 IPC，不依赖 dsh 存活（ADR diag-masking-and-recovery-page）。
/// 骨架 HTML 为编译期常量（无外部依赖不漂移）；动态数据经 JSON 序列化注入后一律
/// <c>textContent</c> 写入——stderr 是上游不可控输出，绝不走 innerHTML 拼接。
/// </summary>
public static class RecoveryPageBuilder
{
    /// <summary>构建覆写当前文档的 JS：先写静态骨架，再以 textContent 回填数据并接线按钮。
    /// 序列化用默认编码器：<c>&lt;</c> 等与全部非 ASCII 一律 \u 转义——payload 落在脚本字符串
    /// 里时天然不含可执行 HTML 形态。</summary>
    /// <param name="reason">人读失败原因（如「运行时进程意外退出」）。</param>
    /// <param name="stderrTail">子进程 stderr 尾部行（supervisor 已在重启前留证）。</param>
    /// <param name="english">按钮/状态文案是否取英文分支（宿主 UI 语言单点）。</param>
    public static string BuildScript(string reason, IReadOnlyList<string> stderrTail, bool english)
    {
        string payload = JsonSerializer.Serialize(new Payload(reason, stderrTail), AppJsonContext.Default.Payload);

        return new StringBuilder("document.documentElement.innerHTML=")
            .Append(AppJsonContext.JsString(Skeleton(english)))
            .Append(";var D=")
            .Append(payload)
            .Append(';')
            .Append(Wire(english))
            .ToString();
    }

    /// <summary>恢复页动态数据帧；internal 供 <see cref="AppJsonContext"/> 源生成注册。</summary>
    /// <param name="Reason">人读失败原因。</param>
    /// <param name="Tail">子进程 stderr 尾部行。</param>
    internal sealed record Payload(string Reason, IReadOnlyList<string> Tail);

    private static string Skeleton(bool english) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>DeepSeek Harness Desktop</title><style>" +
        // 与 boot 页（wwwroot/index.html）同一套 --dshdt-* 回退调色板：亮基暗覆 + Canvas 底 +
        // 墨色单色系；两处各自内联自足（骨架是编译期常量，不外链、不依赖 dsh 主题 CSS）。
        ":root{color-scheme:light dark;--dshdt-label-primary:#0f1115;--dshdt-label-secondary:#61666b;--dshdt-label-tertiary:#81858c;" +
        "--dshdt-border:rgb(0 0 0/10%);--dshdt-brand:#0f1115;--dshdt-surface:rgb(0 0 0/4%)}" +
        "@media (prefers-color-scheme:dark){:root{--dshdt-label-primary:#f9fafb;--dshdt-label-secondary:#cfd3d6;--dshdt-label-tertiary:#adb2b8;" +
        "--dshdt-border:rgb(255 255 255/12%);--dshdt-brand:#f9fafb;--dshdt-surface:rgb(255 255 255/6%)}}" +
        "body{font-family:system-ui,sans-serif;background:Canvas;color:var(--dshdt-label-primary);display:flex;flex-direction:column;" +
        "align-items:center;justify-content:center;height:100vh;gap:14px;margin:0}" +
        ".spin{position:relative;width:20px;height:20px;border:2px solid var(--dshdt-border);border-radius:50%;animation:r .8s linear infinite}" +
        ".spin::after{content:'';position:absolute;inset:-2px;border-radius:inherit;background:conic-gradient(var(--dshdt-brand) 72deg,transparent 0);" +
        "-webkit-mask:radial-gradient(farthest-side,transparent calc(100% - 2px),#000 0);mask:radial-gradient(farthest-side,transparent calc(100% - 2px),#000 0)}" +
        "@keyframes r{to{transform:rotate(360deg)}}" +
        "h2{margin:0;font-size:16px;line-height:24px;font-weight:600;letter-spacing:.08em}" +
        "p{margin:0;color:var(--dshdt-label-tertiary);font-size:13px;line-height:1.6}" +
        "#ddc-tail{max-width:720px;max-height:180px;overflow:auto;background:var(--dshdt-surface);border:1px solid var(--dshdt-border);border-radius:8px;" +
        "padding:10px 14px;font:12px/1.5 ui-monospace,monospace;color:var(--dshdt-label-secondary);white-space:pre-wrap;word-break:break-all;display:none}" +
        ".row{display:flex;gap:12px}button{font:13px system-ui,sans-serif;padding:8px 24px;border-radius:8px;cursor:pointer;line-height:1.5;" +
        "border:1px solid var(--dshdt-border);background:transparent;color:var(--dshdt-label-secondary)}" +
        "button:hover:not(:disabled){color:var(--dshdt-label-primary);opacity:.92}" +
        "button:disabled{opacity:.4;cursor:default}" +
        "#ddc-export{background:var(--dshdt-label-primary);color:Canvas;border-color:transparent}" +
        "#ddc-status{color:var(--dshdt-label-secondary);min-height:1.2em}</style></head>" +
        "<body><div class=\"spin\"></div><h2>DeepSeek Harness Desktop</h2>" +
        "<p id=\"ddc-reason\"></p><div id=\"ddc-tail\"></div><p id=\"ddc-status\"></p>" +
        "<div class=\"row\"><button id=\"ddc-export\">" + UiCopy.RecoveryExportButton(english) + "</button><button id=\"ddc-exit\">" + UiCopy.RecoveryExitButton(english) + "</button></div>" +
        "<p style=\"font-size:12px;color:var(--dshdt-label-tertiary)\">" + UiCopy.RecoveryAutoRetryNote(english) + "</p></body></html>";

    private static string Wire(bool english) =>
        "document.getElementById('ddc-reason').textContent=D.reason;" +
        "var t=document.getElementById('ddc-tail');" +
        "if(D.tail&&D.tail.length){D.tail.forEach(function(l){var d=document.createElement('div');d.textContent=l;t.appendChild(d);});t.style.display='block';}" +
        "function frame(r){try{return (typeof r==='string')?JSON.parse(r):(r||{});}catch(e){return {error:String(e)};}}" +
        "document.getElementById('ddc-export').onclick=async function(){var s=document.getElementById('ddc-status');" +
        "s.textContent='" + UiCopy.RecoveryExporting(english) + "';this.disabled=true;" +
        "try{var o=frame(await window.__ryn.invoke('desktop.diagnostics.export',{}));" +
        "s.textContent=o.path?('" + UiCopy.RecoveryExportedPrefix(english) + "'+o.path):('" + UiCopy.RecoveryExportFailedPrefix(english) + "'+(o.error||'" + UiCopy.RecoveryUnknownReason(english) + "'));}catch(e){s.textContent='" + UiCopy.RecoveryExportFailedPrefix(english) + "'+e;}" +
        "this.disabled=false;};" +
        "document.getElementById('ddc-exit').onclick=async function(){this.disabled=true;" +
        "try{await window.__ryn.invoke('desktop.recovery.exit',{});}catch(e){}};";
}
