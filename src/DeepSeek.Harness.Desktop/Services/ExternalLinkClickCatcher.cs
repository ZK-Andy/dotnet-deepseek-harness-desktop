using System.Text;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>注入到 dsh WebView 的外部链接点击拦截脚本（宿主侧系统集成，不依赖前端）。</summary>
/// <remarks>见 proposed bug-fix ADR open-external-links-in-system-browser。用 capture 阶段监听，
/// 拦截 <c>http(s)</c> 且带 <c>target="_blank"</c> 或指向站外（非同源）的链接，preventDefault 后经
/// <c>window.__ryn.invoke('app.openExternal', {{ url }})</c> 交给宿主 <see cref="ExternalLinkCommandRouter"/>。
/// 站内链接（同源非 _blank）和所有非 http(s) 一律放行，不干扰 SPA 路由 / ryn:// 资源。</remarks>
public static class ExternalLinkClickCatcher
{
    /// <summary>注入用脚本。经 <c>IRynWebView.InjectScriptAsync</c> 以 READY 注入，对当前及后续每页生效。</summary>
    public static string Script { get; } = Build();

    private static string Build()
    {
        // 注意：不要用插值拼入任何来自前端/用户的字符串——脚本是编译期常量，全部字面量。
        var script = new StringBuilder();
        script.Append("(function(){");
        script.Append("if (window.top !== window.self) return;"); // 只处理顶层帧，避免污染跨源 iframe
        script.Append("if (window.__ryn_externalLinkCatcher) return; window.__ryn_externalLinkCatcher = true;");
        script.Append("var ryn = window.__ryn; if (!ryn) return;");
        script.Append("function isExternal(a, origin){");
        script.Append("var href = a.getAttribute('href'); if (!href) return false;");
        script.Append("var u; try { u = new URL(href, origin); } catch(e) { return false; }");
        script.Append("if (u.protocol !== 'http:' && u.protocol !== 'https:') return false;");
        script.Append("if (a.target === '_blank') return true;");
        script.Append("return u.origin !== origin;");
        script.Append("}");
        script.Append("function onClick(e){");
        script.Append("var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;");
        script.Append("if (!a) return;");
        script.Append("if (!isExternal(a, window.location.origin)) return;");
        script.Append("e.preventDefault();");
        script.Append("ryn.invoke('app.openExternal', { url: a.href }).catch(function(){});");
        script.Append("}");
        script.Append("document.addEventListener('click', onClick, true);");
        script.Append("})();");
        return script.ToString();
    }
}
