using Ryn.Callbacks;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主导航回调（Ryn 0.32.0 <c>Ryn.Callbacks</c>）：把外部链接处理从「点击层注入脚本」
/// 迁到「导航层统一拦截」，并给崩溃恢复/横幅门控提供「页面已到达」的权威信号。
/// </summary>
/// <remarks>
/// 一个类可承载两类回调，由源生成器按 <see cref="RynCallbackAttribute"/> 分组生成
/// <c>IRynCallbackRouter</c> 实现并注册为 DI 单例：
/// <list type="bullet">
/// <item><see cref="OnWebViewNavigating"/>：在原生引擎提交导航前裁决——用户发起的站外 http(s) 一律
/// <see cref="NavigationDecision.Block"/> 并交给系统浏览器（经 <see cref="SystemBrowser"/>），同源 SPA
/// 路由 / <c>ryn://</c> / <c>data:</c> 等非 http(s) scheme 放行；宿主程序化导航（崩溃恢复）放行。</item>
/// <item><see cref="OnWebViewNavigated"/>：导航已到达（内容实际提交）后留痕并回调「到达」信号，
/// 供启动横幅门控（较 <c>NavigateAsync</c> 返回的「任务已派发」更权威）。</item>
/// </list>
/// 本类为 instance：源生成器把依赖方法经 DI 解析 <c>GetRequiredService&lt;RynNavigationCallbacks&gt;</c>
/// 后调用，故构造函数依赖必须在 DI 注册（DesktopBootstrap.RegisterServices 用工厂覆盖无参注册）。
/// </remarks>
public sealed class RynNavigationCallbacks
{
    private readonly Func<string, bool> _opener;
    private readonly Action<string>? _log;
    private readonly Action<string>? _notifyLinkFail;
    // 当前页面 origin（如 http://127.0.0.1:41449），供同源判定。随每次 WebViewNavigated 刷新为
    // 实际到达的 origin——崩溃恢复可能把端口漂移（ADR child-process-reaping-port-drift），冻结首启
    // origin 会让漂移后的同源 SPA 路由被误判为外部。初始值由构造注入（首启 webUrl 的 Authority）。
    private string? _currentOrigin;
    private Action? _onNavigatedImpl;

    /// <summary>创建导航回调。</summary>
    /// <param name="opener">打开外部 URL 的委托；null 时默认用系统默认浏览器（见 <see cref="SystemBrowser"/>）。</param>
    /// <param name="log">日志输出（可选）。</param>
    /// <param name="currentOrigin">当前页面 origin（如 <c>http://127.0.0.1:41449</c>）初始值；供同源判定。null 时 <see cref="ExternalLinkPolicy"/> 保守地把一切绝对 http(s) 视为外部。</param>
    /// <param name="notifyLinkFail">外部链接打开失败通知（可选）：携带失败的 URL。由 DesktopBootstrap 接
    /// <c>IRynWebView.EmitEvent</c> 推给页面经 companion 渲染 toast（R2 N2）。委托注入保持本类可单测。</param>
    public RynNavigationCallbacks(
        Func<string, bool>? opener = null,
        Action<string>? log = null,
        string? currentOrigin = null,
        Action<string>? notifyLinkFail = null)
    {
        _opener = opener ?? SystemBrowser.Open;
        _log = log;
        _currentOrigin = currentOrigin;
        _notifyLinkFail = notifyLinkFail;
    }

    /// <summary>
    /// 绑定「导航已到达」回调（单实例可变）。由 DesktopBootstrap 在 <c>_startupNavigationSettled</c>
    /// 声明后注入，取代 <c>RuntimeSupervisor.onNavigated</c> 的「<c>NavigateAsync</c> 返回即触发」——
    /// 本回调由 <see cref="RynCallbackKind.WebViewNavigated"/> 在内容实际提交后触发，是更权威的
    /// 「页面已到达」信号。写入在主线程（<c>app.Build()</c> 后），读在 saucer 原生回调线程，故用
    /// <see cref="Volatile"/> 保证跨线程可见性。
    /// </summary>
    public void SetOnNavigated(Action onNavigated) => Volatile.Write(ref _onNavigatedImpl, onNavigated);

    /// <summary>用户发起的站外 http(s) 导航 → 拦截并交系统浏览器；宿主导航与同源/其它 scheme 放行。</summary>
    /// <param name="context">导航上下文（目标 URL、是否新窗口/重定向/用户发起）。</param>
    [RynCallback(RynCallbackKind.WebViewNavigating)]
    public NavigationDecision OnWebViewNavigating(WebViewNavigatingContext context)
    {
        // 只拦「用户主动发起」的导航：宿主程序化导航（崩溃恢复 NavigateAsync、SPA 内部重定向）
        // 是 IsUserInitiated=false，必须放行，否则恢复流程会被本回调误拦（B1）。
        if (!context.IsUserInitiated)
        {
            return NavigationDecision.Allow;
        }

        // 只对绝对 http(s) 且非当前页同源的导航走外部；放行同源 SPA 路由与非 http(s) scheme
        if (!ExternalLinkPolicy.IsExternalHttpLink(context.Url.ToString(), _currentOrigin, out Uri? url))
        {
            return NavigationDecision.Allow;
        }

        _log?.Invoke($"[nav] 拦截外部导航 → 系统浏览器：{url}");
        bool opened = false;
        try
        {
            opened = _opener(url.ToString());
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[nav] 打开外部链接失败 {url}：{ex.Message}");
        }

        // 打开失败（返回 false 或抛异常）时通知页面给用户可见提示（R2 N2）——fail loud 对用户侧
        // 也生效，而非只对日志 loud。站外链接本就不该把 WebView 导航走（Block），但要让人知道失败。
        if (!opened)
        {
            _notifyLinkFail?.Invoke(url.ToString());
        }

        // 打开成功与否都 Block，避免 WebView 被用户带到外部站；失败已记日志 + 页面 toast
        return NavigationDecision.Block;
    }

    /// <summary>导航已到达：刷新当前 origin、留痕并回调「到达」信号。</summary>
    /// <param name="context">已到达的 URL。</param>
    [RynCallback(RynCallbackKind.WebViewNavigated)]
    public void OnWebViewNavigated(WebViewNavigatedContext context)
    {
        _currentOrigin = context.Url.GetLeftPart(UriPartial.Authority);
        _log?.Invoke($"[nav] 导航已到达：{context.Url}（origin → {_currentOrigin}）");
        Volatile.Read(ref _onNavigatedImpl)?.Invoke();
    }
}
