using System.Text;
using System.Text.Json;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主命令路由：处理 <c>app.openExternal</c>，把外部 URL 交给系统默认浏览器打开。
/// </summary>
/// <remarks>
/// 由 <see cref="RynCommandDispatcher"/> 按前缀收集分发（注册为 <see cref="ICommandRouter"/> 单例）。
/// 打开动作通过 <see cref="Opener"/> 委托注入，默认 <see cref="SystemBrowser"/>；测试注入假开器避免真的弹浏览器。
/// 触发源：Ryn 0.32.0 起由导航层拦截（<see cref="RynNavigationCallbacks"/>）在 <c>WebViewNavigating</c>
/// 里经本命令把站外 URL 交给系统浏览器；历史/已发布 companion 的注入脚本也经 <c>app.openExternal</c> 触达本路由。
/// </remarks>
public sealed class ExternalLinkCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名（注入脚本用 <c>window.__ryn.invoke('app.openExternal', {{ url }})</c> 触发；导航层亦经此落系统浏览器）。</summary>
    public const string CommandName = "app.openExternal";

    private readonly Func<string, bool> _opener;
    private readonly Action<string>? _log;

    /// <summary>创建路由。</summary>
    /// <param name="opener">打开 URL 的委托；null 时默认用系统默认浏览器。</param>
    /// <param name="log">日志委托（可选；生产传 <see cref="HostLog"/>.Write，测试注入收集器）。</param>
    public ExternalLinkCommandRouter(Func<string, bool>? opener = null, Action<string>? log = null)
    {
        _opener = opener ?? OpenWithDefaultBrowser;
        _log = log;
    }

    /// <inheritdoc />
    public bool CanRoute(string command) => string.Equals(command, CommandName, StringComparison.Ordinal);

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!CanRoute(command))
        {
            throw new RynCommandNotFoundException(command);
        }

        // 请求体是 JSON：{ "url": "https://..." }
        string body = Encoding.UTF8.GetString(args.Span);
        string? href = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("url", out JsonElement urlProp) &&
                urlProp.ValueKind == JsonValueKind.String)
            {
                href = urlProp.GetString();
            }
        }
        catch (JsonException)
        {
            // 非 JSON——按无 url 处理，走拒绝
        }

        // 防御性二次校验：只打开绝对 http/https，且非空
        href = href?.Trim();
        if (string.IsNullOrWhiteSpace(href) ||
            !ExternalLinkPolicy.IsExternalHttpLink(href, currentOrigin: null, out _))
        {
            _log?.Invoke($"[external-link] 拒绝打开的 URL：{(string.IsNullOrWhiteSpace(href) ? "(空)" : href)}");
            return ValueTask.FromResult("null");
        }

        bool opened = false;
        try
        {
            opened = _opener(href);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[external-link] 打开失败 {href}：{ex.Message}");
        }

        // 成功与否都返回成功帧（避免 JS 抛 IPC 错误；失败已记日志）
        return ValueTask.FromResult(opened ? "{}" : "null");
    }

    /// <summary>
    /// 默认打开器：委托给 <see cref="SystemBrowser"/>（Linux 走 <c>xdg-open</c> 并重定向子进程输出，
    /// 浏览器自身后台报错不回灌应用终端；其余平台 <c>Process.Start(UseShellExecute=true)</c>）。
    /// </summary>
    private static bool OpenWithDefaultBrowser(string url) => SystemBrowser.Open(url);
}
