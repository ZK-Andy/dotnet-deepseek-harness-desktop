using System.Text;
using System.Text.Json;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主命令路由：<c>desktop.companion.setLocale</c>——companion 桥接 dsh 语言变更
/// （监听 <c>html[lang]</c> 上报），宿主据此驱动托盘菜单重建与横幅文案分支（ADR host-ui-locale）。
/// </summary>
/// <remarks>
/// locale 桥是增强能力：坏 JSON/非法 locale/无变化一律静默忽略（返回 null，不报 IPC 错），
/// 上报方 invoke 也自带 catch——绝不影响安装/更新主链路。
/// </remarks>
public sealed class CompanionLocaleCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名。</summary>
    public const string CommandName = "desktop.companion.setLocale";

    private readonly UiLocale _uiLocale;
    private readonly Action<string>? _log;

    /// <summary>创建路由。</summary>
    /// <param name="uiLocale">宿主 UI 语言单例。</param>
    /// <param name="log">日志委托（可选；生产传 HostLog.Write）。</param>
    public CompanionLocaleCommandRouter(UiLocale uiLocale, Action<string>? log = null)
    {
        _uiLocale = uiLocale;
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

        string? locale = null;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(args.Span));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("locale", out JsonElement localeProp) &&
                localeProp.ValueKind == JsonValueKind.String)
            {
                locale = localeProp.GetString();
            }
        }
        catch (JsonException)
        {
            // 坏载荷按未上报处理（增强能力，静默忽略）
        }

        // 防御校验：html[lang] 形态（zh-CN/en），2-8 字母段 + 可选子段；超面即忽略
        if (string.IsNullOrWhiteSpace(locale) || locale.Length > 35 || !IsPlausibleLocale(locale))
        {
            return ValueTask.FromResult("null");
        }

        _uiLocale.Set(locale);
        return ValueTask.FromResult("{}");
    }

    /// <summary>宽松合法性：字母段（- 分隔）形态即可，具体语言分支由 <see cref="UiLocale.IsEnglish"/> 判。</summary>
    private static bool IsPlausibleLocale(string locale)
    {
        foreach (string part in locale.Split('-'))
        {
            if (part.Length is < 2 or > 8 || !part.All(char.IsAsciiLetterOrDigit))
            {
                return false;
            }
        }

        return true;
    }
}
