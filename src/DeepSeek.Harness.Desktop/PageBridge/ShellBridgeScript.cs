namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// <c>window.__shell</c> 注入脚本构建（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：
/// 页面侧只保留薄壳——Promise 挂账、事件表与上行投递；校验、分发与超时兜底在宿主侧。
/// 上行走包自带的 <c>invokeCSharpAction</c>（NativeWebView 文档声明的唯一 JS→宿主通道），
/// 下行由宿主经 <c>InvokeScript</c> 调 <c>_resolve</c>/<c>_emit</c>。
/// 幂等：同 nonce 的重复注入直接返回（导航后 nonce 变化，旧桥被替换）。
/// </summary>
internal static class ShellBridgeScript
{
    /// <summary>页面侧 invoke 超时（毫秒）：宿主侧路由不设全局超时，页面先兜。</summary>
    public const int InvokeTimeoutMilliseconds = 15000;

    /// <summary>构建注入脚本。</summary>
    /// <param name="nonce">当前文档 nonce（JSON 字符串编码后嵌入）。</param>
    /// <returns>自执行脚本。</returns>
    public static string Build(string nonce)
    {
        string nonceJs = AppJsonContext.JsString(nonce);
        return "(function(){"
            + "var N=" + nonceJs + ";"
            + "if(window.__shell&&window.__shell.__nonce===N){return;}"
            + "var pending={},seq=0,handlers={};"
            + "function post(m){invokeCSharpAction(JSON.stringify(m));}"
            + "function invoke(cmd,args){return new Promise(function(resolve,reject){"
            + "var id=++seq;"
            + "var timer=setTimeout(function(){if(pending[id]){delete pending[id];reject(new Error('shell invoke timeout: '+cmd));}},"
            + InvokeTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ");"
            + "pending[id]={resolve:resolve,reject:reject,timer:timer};"
            + "try{post({v:"
            + ShellBridgeProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",n:N,id:id,cmd:cmd,args:args===undefined?null:args});}"
            + "catch(e){clearTimeout(timer);delete pending[id];reject(e);}});}"
            + "function on(name,fn){(handlers[name]=handlers[name]||[]).push(fn);}"
            + "function off(name,fn){var a=handlers[name];if(!a){return;}var i=a.indexOf(fn);if(i>=0){a.splice(i,1);}}"
            + "function resolve(id,ok,payload){var p=pending[id];if(!p){return;}delete pending[id];clearTimeout(p.timer);"
            + "if(ok){p.resolve(payload);}else{p.reject(new Error((payload&&payload.error)||'shell command failed'));}}"
            + "function emit(name,payload){var a=(handlers[name]||[]).slice();for(var i=0;i<a.length;i++){try{a[i](payload);}catch(e){}}}"
            + "window.__shell={invoke:invoke,on:on,off:off,_resolve:resolve,_emit:emit,__nonce:N};"
            + "})()";
    }
}
