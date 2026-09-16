using System.Text.Json;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 宿主命令路由：<c>desktop.ui.getLocale</c>——<c>wwwroot</c> 引导页在 dsh 启动前向宿主
/// 拉取当前 UI 语言（ADR host-ui-locale 的页面侧补齐）。
/// </summary>
/// <remarks>
/// 为什么是「拉」而不是宿主「推」：引导页在窗口创建后加载，宿主早期注入的脚本可能先于页面
/// 脚本执行而丢失（无就绪信号）；页面主动 invoke 无时序竞态，宿主何时装配完成都不影响。
/// 语言值即 <see cref="UiLocale.Current"/>（上次 dsh 上报的持久值或 OS locale 兜底），
/// 页面据此取中/英分支；命令不可用时页面保持中文。
/// </remarks>
public sealed class UiLocaleCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名。</summary>
    public const string CommandName = "desktop.ui.getLocale";

    private readonly UiLocale _uiLocale;

    /// <summary>创建路由。</summary>
    /// <param name="uiLocale">宿主 UI 语言单点。</param>
    public UiLocaleCommandRouter(UiLocale uiLocale) => _uiLocale = uiLocale;

    /// <inheritdoc />
    public bool CanRoute(string command) => string.Equals(command, CommandName, StringComparison.Ordinal);

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!CanRoute(command))
        {
            throw new RynCommandNotFoundException(command);
        }

        return ValueTask.FromResult(Frame(_uiLocale.Current));
    }

    /// <summary>状态帧 <c>{"locale":"zh-CN"}</c>；键名 = 属性名经 CamelCase 策略推导，改属性名即改线协议。</summary>
    /// <param name="Locale">当前 UI 语言标记。</param>
    internal sealed record UiLocaleFrame(string Locale);

    private static string Frame(string locale) =>
        JsonSerializer.Serialize(new UiLocaleFrame(locale), AppJsonContext.Default.UiLocaleFrame);
}
