namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// UI 语言持久化的文件实现（<see cref="IUiLocaleStore"/>）：落在当前 desktop profile 目录，
/// 与端口/PID 记忆同族（<c>~/.dsh/profiles/dotnet-desktop/ui-locale</c>）。
/// 读失败或内容损坏回 null（上层回退 OS locale）；写失败仅告警——两者都只是「下次启动的语言
/// 起点」，绝不阻断启动或语言切换。
/// </summary>
/// <param name="log">日志回调（可选；生产传 HostLog.Write）。</param>
public sealed class DesktopUiLocaleStore(Action<string>? log = null) : IUiLocaleStore
{
    /// <summary>状态文件名（profile 目录下，与端口/PID 记忆并列）。</summary>
    private const string FileName = "ui-locale";

    /// <summary>读取上次记住的 locale；无记录、不可读或损坏一律 null。</summary>
    public string? Load()
    {
        string path = ResolvePath();
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string text = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // DSH_HOME 暂不可读：回退 OS locale（fail loud 无意义——语言起点不值得打断启动）
            log?.Invoke($"[host] 读取上次 UI 语言失败（将回退 OS locale）：{ex.Message}");
            return null;
        }
    }

    /// <summary>记住本次 locale（尽力而为；写失败仅导致下次启动回退 OS locale）。</summary>
    /// <param name="locale">本次生效的 locale 原值。</param>
    public void Save(string locale)
    {
        string path = ResolvePath();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, locale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写失败仅影响下次启动的语言起点，不阻断本次运行
            log?.Invoke($"[host] 写 UI 语言状态失败（下次启动将回退 OS locale）：{ex.Message}");
        }
    }

    private static string ResolvePath() => HarnessRuntimeHost.ResolveProfileStatePath(FileName);
}
