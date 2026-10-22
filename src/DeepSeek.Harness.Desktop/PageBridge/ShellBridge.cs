using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Core.Platform;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// <c>window.__shell</c> 桥接本体（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：信封校验、
/// 命令分发、事件推送与注入时机全在此。平台面只经 <see cref="IWebViewSurface"/>
/// （Avalonia 适配器见 <see cref="NativeWebViewSurface"/>），故桥接可在 headless 测试里完整跑。
/// 来源判据是 nonce + 顶层 URL 双条（消息参数只有 <c>Body</c>，没有 origin 可信来源）：
/// nonce 每次成功导航轮换并重注入（注入对任何成功完成的文档都执行，故 nonce 不证明来源）；
/// 顶层 URL 由最近一次成功 <c>NavigationCompleted</c> 记录，来源由它兜住。
/// 分发在后台任务执行（路由含 I/O），回包经适配器封送回 UI 线程。
/// </summary>
internal sealed class ShellBridge : IDisposable
{
    private static readonly IServiceProvider s_emptyServices = new EmptyServiceProvider();

    private readonly IWebViewSurface _surface;
    private readonly IReadOnlyList<ICommandRouter> _routers;
    private readonly ISystemBrowser _browser;
    private readonly string _shellOrigin;
    private readonly Action<string> _log;
    private readonly bool _macChrome;
    private readonly int _captionHeightPx;
    private readonly CancellationTokenSource _cts = new();
    // 令牌在构造期取一次：Dispose 会释放 CTS，之后再读 `_cts.Token` 会抛 ObjectDisposedException，
    // 而 fire-and-forget 的注入/分发路径没有调用方可承接该异常（静默丢失或未观测）。
    private readonly CancellationToken _token;
    private readonly object _gate = new();
    private string _nonce = NewNonce();
    private string? _currentUrl;
    private bool _attached;
    private bool _disposed;

    /// <summary>创建桥接（尚未订阅，须调用 <see cref="Attach"/>）。</summary>
    /// <param name="surface">网页视图可测面。</param>
    /// <param name="routers">命令路由表（<c>CanRoute</c> 顺序查找）。</param>
    /// <param name="shellOrigin">壳 origin（授权外壳 authority，如 <c>http://127.0.0.1:41449</c>）。</param>
    /// <param name="browser">系统浏览器端口（外链拦截）。</param>
    /// <param name="macChrome">顶栏是否 macOS 形态（写进 <c>__shell_titlebar</c>）。</param>
    /// <param name="captionHeightPx">顶栏高度（CSS 像素）。</param>
    /// <param name="log">日志委托。</param>
    public ShellBridge(
        IWebViewSurface surface,
        IReadOnlyList<ICommandRouter> routers,
        string shellOrigin,
        ISystemBrowser browser,
        bool macChrome,
        int captionHeightPx,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(routers);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(log);
        _surface = surface;
        _routers = routers;
        _shellOrigin = shellOrigin;
        _browser = browser;
        _macChrome = macChrome;
        _captionHeightPx = captionHeightPx;
        _log = log;
        _token = _cts.Token;
    }

    /// <summary>订阅可测面；重复调用为 no-op。注入只在成功导航完成后发生（窗口构造时文档尚不存在）。</summary>
    public void Attach()
    {
        lock (_gate)
        {
            if (_attached || _disposed)
            {
                return;
            }

            _attached = true;
        }

        _surface.MessageReceived += OnMessageReceived;
        _surface.NavigationStarted += OnNavigationStarted;
        _surface.NavigationCompleted += OnNavigationCompleted;
        _surface.NewWindowRequested += OnNewWindowRequested;
    }

    /// <summary>推一条事件脚本：已销毁/取消时静默丢弃（事件是通知面，消费者依赖后续状态补送）；
    /// 其余失败留痕（`事件推送失败`）。</summary>
    /// <param name="name">事件名。</param>
    /// <param name="payloadJson">载荷 JSON 原文。</param>
    public void EmitEvent(string name, string payloadJson)
    {
        if (_disposed)
        {
            return;
        }

        _ = EmitAsync(ShellBridgeProtocol.EmitScript(name, payloadJson));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts.Cancel();
        _surface.MessageReceived -= OnMessageReceived;
        _surface.NavigationStarted -= OnNavigationStarted;
        _surface.NavigationCompleted -= OnNavigationCompleted;
        _surface.NewWindowRequested -= OnNewWindowRequested;
        _cts.Dispose();
    }

    /// <summary>消息入口：信封/nonce 校验 → 顶层文档判据 → 后台分发。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">消息参数。</param>
    private void OnMessageReceived(object? sender, PageMessageEventArgs e)
    {
        string nonce;
        lock (_gate)
        {
            nonce = _nonce;
        }

        if (!ShellBridgeProtocol.TryParse(e.Body, nonce, out long id, out string command, out string argsJson))
        {
            _log("[shell-bridge] 丢弃信封不合法的消息");
            return;
        }

        if (!IsShellDocument())
        {
            _log("[shell-bridge] 丢弃来源不明的消息（顶层文档不是壳 origin）");
            return;
        }

        _ = Task.Run(() => DispatchAsync(id, command, argsJson));
    }

    /// <summary>顶层文档判据：最近一次已完成的导航必须落在壳 origin。</summary>
    /// <returns>是壳文档时为 true。</returns>
    private bool IsShellDocument()
    {
        string? current;
        lock (_gate)
        {
            current = _currentUrl;
        }

        if (current is null || !Uri.TryCreate(current, UriKind.Absolute, out Uri? url))
        {
            return false;
        }

        return string.Equals(url.GetLeftPart(UriPartial.Authority), _shellOrigin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>分发命令并在完成后回包；异常转错误帧，取消路径静默收口。</summary>
    /// <param name="id">请求 id。</param>
    /// <param name="command">命令 ID。</param>
    /// <param name="argsJson">参数帧 JSON 原文。</param>
    /// <returns>完成的任务。</returns>
    private async Task DispatchAsync(long id, string command, string argsJson)
    {
        CancellationToken ct = _token;
        ICommandRouter? router = null;
        foreach (ICommandRouter candidate in _routers)
        {
            if (candidate.CanRoute(command))
            {
                router = candidate;
                break;
            }
        }

        if (router is null)
        {
            await RespondAsync(id, ok: false, AppJsonContext.Error("未知命令：" + command), ct).ConfigureAwait(false);
            return;
        }

        string payload;
        try
        {
            byte[] args = Encoding.UTF8.GetBytes(argsJson);
            payload = await router.RouteAsync(command, args, s_emptyServices, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload))
            {
                payload = "null";
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await RespondAsync(id, ok: false, AppJsonContext.Error(ex.Message), ct).ConfigureAwait(false);
            return;
        }

        await RespondAsync(id, ok: true, payload, ct).ConfigureAwait(false);
    }

    /// <summary>回包（失败只留痕：页面侧 Promise 会自行超时）。</summary>
    /// <param name="id">请求 id。</param>
    /// <param name="ok">成功/失败。</param>
    /// <param name="payloadJson">载荷 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成的任务。</returns>
    private async Task RespondAsync(long id, bool ok, string payloadJson, CancellationToken ct)
    {
        try
        {
            await _surface.InvokeScriptAsync(ShellBridgeProtocol.ResolveScript(id, ok, payloadJson), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退出/关窗：页面不再需要回包。
        }
        catch (Exception ex)
        {
            _log($"[shell-bridge] 回包失败（id={id.ToString(CultureInfo.InvariantCulture)}）：{ex.Message}");
        }
    }

    /// <summary>导航开始：用户可发起的外站 http(s) 一律取消并交系统浏览器；同源/非 http(s) 放行。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">导航参数。</param>
    private void OnNavigationStarted(object? sender, PageNavigationEventArgs e)
    {
        if (e.Url is not { } url)
        {
            return;
        }

        if (!ExternalLinkPolicy.IsExternalHttpLink(url.ToString(), _shellOrigin, out Uri? external))
        {
            return;
        }

        e.Cancel = true;
        OpenExternal(external);
    }

    /// <summary>新窗口请求：一律标记已处理（不在壳内开窗），外站 http(s) 交系统浏览器。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">导航参数。</param>
    private void OnNewWindowRequested(object? sender, PageNavigationEventArgs e)
    {
        e.Handled = true;
        if (e.Url is { } url && ExternalLinkPolicy.IsExternalHttpLink(url.ToString(), _shellOrigin, out Uri? external))
        {
            OpenExternal(external);
        }
    }

    /// <summary>导航完成：成功时刷新顶层 URL、轮换 nonce 并重注入桥；失败/取消保留当前文档的状态
    /// （显示中的仍是旧文档，换 nonce 会让它所有 invoke 被拒且无恢复路径）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">导航参数。</param>
    private void OnNavigationCompleted(object? sender, PageNavigationEventArgs e)
    {
        if (!e.IsSuccess)
        {
            // 失败/取消的导航保留当前文档的 nonce 与 origin：显示的仍是旧文档，换 nonce 会让它所有
            // invoke 被拒且无恢复路径。外链失败属 NavigationStarted 取消的预期路径（不逐次留痕）；
            // 壳 origin 自身加载失败要带 URL 留痕，否则只剩健康探针的迁移日志。
            string? failed = e.Url?.ToString();
            if (failed is null || !ExternalLinkPolicy.IsExternalHttpLink(failed, _shellOrigin, out _))
            {
                _log($"[shell-bridge] 导航未完成：{failed ?? "(URL 未知)"}（保留当前文档的 nonce 与 origin）");
            }

            return;
        }

        lock (_gate)
        {
            _currentUrl = e.Url?.ToString();
            _nonce = NewNonce();
        }

        _ = InjectBridgeAsync();
    }

    /// <summary>注入桥（失败只留痕：桥是增强面，不拖垮页面）。</summary>
    /// <returns>完成的任务。</returns>
    private async Task InjectBridgeAsync()
    {
        CancellationToken ct = _token;
        string nonce;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            nonce = _nonce;
        }

        try
        {
            await _surface.InvokeScriptAsync(
                ShellBridgeScript.Build(nonce, _macChrome, _captionHeightPx), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退出/关窗。
        }
        catch (Exception ex)
        {
            _log($"[shell-bridge] 桥注入失败：{ex.Message}");
        }
    }

    /// <summary>推一条事件脚本（失败只留痕）。</summary>
    /// <param name="script">事件脚本。</param>
    /// <returns>完成的任务。</returns>
    private async Task EmitAsync(string script)
    {
        try
        {
            await _surface.InvokeScriptAsync(script, _token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退出/关窗。
        }
        catch (Exception ex)
        {
            _log($"[shell-bridge] 事件推送失败：{ex.Message}");
        }
    }

    /// <summary>交系统浏览器；打开失败推 <see cref="HostCommandIds.ExternalLinkOpenerFailed"/> 给页面提示。</summary>
    /// <param name="url">外站 URL。</param>
    private void OpenExternal(Uri url)
    {
        bool opened = false;
        try
        {
            opened = _browser.Open(url.ToString());
        }
        catch (Exception ex)
        {
            _log($"[nav] 打开外部链接失败 {url}：{ex.Message}");
        }

        if (opened)
        {
            return;
        }

        EmitEvent(
            HostCommandIds.ExternalLinkOpenerFailed,
            JsonSerializer.Serialize(
                new ExternalLinkOpenerFailedFrame(url.ToString()),
                AppJsonContext.Default.ExternalLinkOpenerFailedFrame));
    }

    /// <summary>新 nonce（128 位十六进制）。</summary>
    /// <returns>nonce 文本。</returns>
    private static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>路由参数面的空服务提供者（本仓路由不使用该参数；不额外引入容器）。</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        /// <inheritdoc />
        public object? GetService(Type serviceType) => null;
    }
}
