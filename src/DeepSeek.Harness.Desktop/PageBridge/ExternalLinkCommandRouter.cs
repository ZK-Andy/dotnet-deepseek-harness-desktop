using System.Text;
using System.Text.Json;
using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 宿主命令路由：处理 <c>app.openExternal</c>，把外部 URL 交给系统默认浏览器打开。
/// </summary>
/// <remarks>
/// 由页面桥按 <see cref="ICommandRouter.CanRoute"/> 收集分发（注册为路由单例）。
/// 打开动作经 <see cref="ISystemBrowser"/> 端口（ADR host-boundary-ports）：平台面（xdg-open /
/// shell execute）在 Infrastructure 实现，测试注入假开器避免真的弹浏览器。
/// 触发源：导航层在 <c>NavigationStarted</c> 拦截站外 URL 后直接经系统浏览器端口打开；页面脚本
/// （companion）亦经 <c>app.openExternal</c> 触达本路由。
/// </remarks>
public sealed class ExternalLinkCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名（页面脚本用 <c>window.__shell.invoke('app.openExternal', {{ url }})</c> 触发；导航层亦经此落系统浏览器）。</summary>
    public const string CommandName = HostCommandIds.OpenExternal;

    private readonly ISystemBrowser _browser;
    private readonly Action<string>? _log;

    /// <summary>创建路由。</summary>
    /// <param name="browser">系统浏览器端口（平台面在 Infrastructure 实现）。</param>
    /// <param name="log">日志委托（可选；生产传 <see cref="HostLog"/>.Write，测试注入收集器）。</param>
    public ExternalLinkCommandRouter(ISystemBrowser browser, Action<string>? log = null)
    {
        _browser = browser;
        _log = log;
    }

    /// <inheritdoc />
    public bool CanRoute(string command) => string.Equals(command, CommandName, StringComparison.Ordinal);

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args, CancellationToken cancellationToken)
    {
        if (!CanRoute(command))
        {
            throw new CommandNotFoundException(command);
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
            opened = _browser.Open(href);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[external-link] 打开失败 {href}：{ex.Message}");
        }

        // 成功与否都返回成功帧（避免 JS 抛 IPC 错误；失败已记日志）
        return ValueTask.FromResult(opened ? "{}" : "null");
    }
}
