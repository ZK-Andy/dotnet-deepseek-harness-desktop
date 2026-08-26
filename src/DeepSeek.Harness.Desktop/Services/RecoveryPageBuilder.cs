using System.Text;
using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services;

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
	public static string BuildScript(string reason, IReadOnlyList<string> stderrTail)
	{
		var payload = JsonSerializer.Serialize(new Payload(reason, stderrTail), AppJsonContext.Default.Payload);

		return new StringBuilder("document.documentElement.innerHTML=")
			.Append(JsonSerializer.Serialize(Skeleton, AppJsonContext.Default.String))
			.Append(";var D=")
			.Append(payload)
			.Append(';')
			.Append(Wire)
			.ToString();
	}

	/// <summary>恢复页动态数据帧；internal 供 <see cref="AppJsonContext"/> 源生成注册。</summary>
	/// <param name="Reason">人读失败原因。</param>
	/// <param name="Tail">子进程 stderr 尾部行。</param>
	internal sealed record Payload(string Reason, IReadOnlyList<string> Tail);

	private const string Skeleton =
		"<!doctype html><html><head><meta charset=\"utf-8\"><title>DeepSeek Harness Desktop</title><style>" +
		"body{font-family:system-ui,sans-serif;background:#0f0f13;color:#e6e6ea;display:flex;flex-direction:column;" +
		"align-items:center;justify-content:center;height:100vh;gap:14px;margin:0}" +
		".spin{width:36px;height:36px;border:3px solid #2a2a3a;border-top-color:#7c3aed;border-radius:50%;animation:r 1s linear infinite}" +
		"@keyframes r{to{transform:rotate(360deg)}}" +
		"h2{margin:0;font-size:18px}p{margin:0;color:#b9b9c6}" +
		"#ddc-tail{max-width:720px;max-height:180px;overflow:auto;background:#17171f;border:1px solid #2a2a3a;border-radius:8px;" +
		"padding:10px 14px;font:12px/1.5 ui-monospace,monospace;color:#9a9aa8;white-space:pre-wrap;word-break:break-all;display:none}" +
		".row{display:flex;gap:12px}button{font:14px system-ui,sans-serif;padding:8px 18px;border-radius:8px;cursor:pointer;" +
		"border:1px solid #3a3a4a;background:#22222e;color:#e6e6ea}button:hover{background:#2a2a38}" +
		"#ddc-status{color:#7c8cff;min-height:1.2em}</style></head>" +
		"<body><div class=\"spin\"></div><h2>DeepSeek Harness Desktop</h2>" +
		"<p id=\"ddc-reason\"></p><div id=\"ddc-tail\"></div><p id=\"ddc-status\"></p>" +
		"<div class=\"row\"><button id=\"ddc-export\">导出诊断包</button><button id=\"ddc-exit\">退出应用</button></div>" +
		"<p style=\"font-size:12px;color:#6a6a78\">系统正在自动重试；恢复后本页会自动消失。</p></body></html>";

	private const string Wire =
		"document.getElementById('ddc-reason').textContent=D.reason;" +
		"var t=document.getElementById('ddc-tail');" +
		"if(D.tail&&D.tail.length){D.tail.forEach(function(l){var d=document.createElement('div');d.textContent=l;t.appendChild(d);});t.style.display='block';}" +
		"function frame(r){try{return (typeof r==='string')?JSON.parse(r):(r||{});}catch(e){return {error:String(e)};}}" +
		"document.getElementById('ddc-export').onclick=async function(){var s=document.getElementById('ddc-status');" +
		"s.textContent='正在导出…';this.disabled=true;" +
		"try{var o=frame(await window.__ryn.invoke('desktop.diagnostics.export',{}));" +
		"s.textContent=o.path?('已导出：'+o.path):('导出失败：'+(o.error||'未知原因'));}catch(e){s.textContent='导出失败：'+e;}" +
		"this.disabled=false;};" +
		"document.getElementById('ddc-exit').onclick=async function(){this.disabled=true;" +
		"try{await window.__ryn.invoke('desktop.recovery.exit',{});}catch(e){}};";
}
