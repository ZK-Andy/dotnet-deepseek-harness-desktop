using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主命令路由：处理 <c>app.openExternal</c>，把 WebView 注入脚本上报的外部 URL 交给系统默认浏览器打开。
/// </summary>
/// <remarks>
/// 由 <see cref="RynCommandDispatcher"/> 按前缀收集分发（注册为 <see cref="ICommandRouter"/> 单例）。
/// 打开动作通过 <see cref="Opener"/> 委托注入，默认 <c>Process.Start(UseShellExecute=true)</c>；测试注入假开器避免真的弹浏览器。
/// </remarks>
public sealed class ExternalLinkCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名（注入脚本用 <c>window.__ryn.invoke('app.openExternal', {{ url }})</c> 触发）。</summary>
    public const string CommandName = "app.openExternal";

    private readonly Func<string, bool> _opener;
    private readonly TextWriter? _log;

    /// <summary>创建路由。</summary>
    /// <param name="opener">打开 URL 的委托；null 时默认用系统默认浏览器。</param>
    /// <param name="log">日志输出（可选）。</param>
    public ExternalLinkCommandRouter(Func<string, bool>? opener = null, TextWriter? log = null)
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
                doc.RootElement.TryGetProperty("url", out var urlProp) &&
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
            _log?.WriteLine($"[external-link] 拒绝打开的 URL：{(string.IsNullOrWhiteSpace(href) ? "(空)" : href)}");
            return ValueTask.FromResult("null");
        }

        bool opened = false;
        try
        {
            opened = _opener(href);
        }
        catch (Exception ex)
        {
            _log?.WriteLine($"[external-link] 打开失败 {href}：{ex.Message}");
        }

        // 成功与否都返回成功帧（避免 JS 抛 IPC 错误；失败已记日志）
        return ValueTask.FromResult(opened ? "{}" : "null");
    }

    /// <summary>
    /// 默认打开器：Linux 走 <c>xdg-open</c> 并重定向子进程输出（浏览器自身的后台报错
    /// 不回灌应用终端）；其余平台 <c>Process.Start(UseShellExecute=true)</c> 交系统默认处理。
    /// </summary>
    private static bool OpenWithDefaultBrowser(string url)
    {
        var psi = new ProcessStartInfo();
        if (OperatingSystem.IsLinux())
        {
            psi.FileName = "xdg-open";
            psi.ArgumentList.Add(url);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        else
        {
            psi.FileName = url;
            psi.UseShellExecute = true;
        }

        using var p = Process.Start(psi);
        if (p is null)
        {
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            // 必须持续排空重定向缓冲区，否则子进程写满管道会阻塞；内容刻意丢弃
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        return !p.HasExited;
    }
}
