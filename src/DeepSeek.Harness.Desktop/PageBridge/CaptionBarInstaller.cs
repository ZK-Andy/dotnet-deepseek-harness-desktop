using Ryn.Core;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 自绘顶栏注入用例（ADR 组合根机制收官终局的 B3：脚本构建与三路注入自启动排序器下沉，排序器只留
/// 一行触发）。三路按调用序：<b>注册</b>（<see cref="PagePump.RegisterCaptionBarScriptWhenReadyAsync"/>——唯一不靠导航事件
/// 覆盖后续每次加载的一路；holder 页 <c>location.reload()</c> 进真 UI 是同 URL 重载，mac/win 导航回调不发）；
/// <b>接线即注入</b>（覆盖「注册只对后续文档生效」的当窗空档）；<b>常驻导航钩子</b>（每次到达后重挂，三路
/// 共用同一份脚本，语言由脚本执行期按 <c>html[lang]</c> 现取）。三路幂等（脚本自带守卫），失败仅留痕。
/// </summary>
internal sealed class CaptionBarInstaller
{
    private readonly CurrentWindowAccessor _windowAccessor;
    private readonly IRynWebView _webView;
    private readonly RynNavigationCallbacks _callbacks;
    private readonly CaptionBarOptions _captionBar;
    private readonly UiLocale _uiLocale;
    private readonly CancellationTokenSource _supervisorCts;
    // 「首次导航到达补注册顶栏脚本」的一次性闩：该到达事件只在 webview 就绪后发，
    // 补注册只做一次，避免每次导航都新增一条引擎注册。
    private int _reregistered;

    /// <summary>创建顶栏注入用例（构造数据注入；脚本在 <see cref="Install"/> 时点按当前语言构建）。</summary>
    /// <param name="windowAccessor">当前窗口访问器（注入与脚本求值的目标）。</param>
    /// <param name="webView">网页视图（Ryn 延迟代理，Build 后可解析；未就绪的语义在 <see cref="PagePump"/> 内）。</param>
    /// <param name="callbacks">导航回调集（常驻到达钩子的挂点）。</param>
    /// <param name="captionBar">自绘标题栏参数家（ADR frameless-uniform-caption-bar）。</param>
    /// <param name="uiLocale">宿主 UI 语言单点（ADR host-ui-locale）。</param>
    /// <param name="supervisorCts">监督器取消令牌源（注入侧取消面；寿命 = 本次 Run，容器单例）。</param>
    public CaptionBarInstaller(
        CurrentWindowAccessor windowAccessor,
        IRynWebView webView,
        RynNavigationCallbacks callbacks,
        CaptionBarOptions captionBar,
        UiLocale uiLocale,
        CancellationTokenSource supervisorCts)
    {
        _windowAccessor = windowAccessor;
        _webView = webView;
        _callbacks = callbacks;
        _captionBar = captionBar;
        _uiLocale = uiLocale;
        _supervisorCts = supervisorCts;
    }

    /// <summary>三路注入（调用序＝注册前的常驻钩子挂载 → 当窗即注入 → 每页加载注册）。</summary>
    public void Install()
    {
        CancellationToken ct = _supervisorCts.Token;
        // macChrome = 是否启用原生 macOS chrome（判据单点见 MacNativeChrome）。
        bool macChrome = MacNativeChrome.IsEnabled;
        string script = CaptionBar.Build(_captionBar.HeightPx, _uiLocale, macChrome);
        Func<IRynWebView> webView = () => _webView;
        _callbacks.SetOnNavigatedPersistent(() => Task.Run(() =>
        {
            _ = PagePump.InjectCaptionBarWhenReadyAsync(_windowAccessor, script, ct);
            // 首次到达补注册一次（该事件只在 webview 就绪后发）：到点预算折算出的注册之外再保一路「后到
            // 但确实活着」的注册时机。
            if (Interlocked.Exchange(ref _reregistered, 1) == 0)
            {
                _ = PagePump.RegisterCaptionBarScriptWhenReadyAsync(webView, script, ct);
            }
        }));
        _ = Task.Run(() => PagePump.InjectCaptionBarWhenReadyAsync(_windowAccessor, script, ct));
        _ = Task.Run(() => PagePump.RegisterCaptionBarScriptWhenReadyAsync(webView, script, ct));
    }
}
