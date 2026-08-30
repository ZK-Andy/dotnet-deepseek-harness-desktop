using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services.Tray;

/// <summary>
/// 「关闭时最小化到托盘」偏好：持久化于 <c>&lt;DSH_HOME&gt;/desktop-preferences.json</c>。
/// 缺文件、损坏或字段缺失一律回退默认 <see langword="true"/>——与历史行为
/// （托盘可用即隐藏）一致，存量用户升级零感知。线程安全：IPC 写入线程与
/// UI 线程 Closing 回调并发读写。
/// </summary>
public sealed class CloseBehaviorPreference
{
    /// <summary>持久化文件名（置于 DSH_HOME 根，home 层数据随生态互通）。</summary>
    public const string FileName = "desktop-preferences.json";

    private readonly string _filePath;
    private readonly object _lock = new();
    private bool _hideOnClose;

    /// <summary>创建偏好实例并从 <paramref name="filePath"/> 装载。</summary>
    public CloseBehaviorPreference(string filePath)
    {
        _filePath = filePath;
        _hideOnClose = Load(filePath);
    }

    /// <summary>关闭按钮是否隐藏到托盘（false = 直接退出）。</summary>
    public bool HideOnClose
    {
        get { lock (_lock) { return _hideOnClose; } }
    }

    /// <summary>写入偏好。内存值先落（读方即刻生效），磁盘失败由调用方按错误帧处理。</summary>
    public void Set(bool hideOnClose)
    {
        lock (_lock)
        {
            _hideOnClose = hideOnClose;
            Write(_filePath, hideOnClose);
        }
    }

    /// <summary>读取持久化值；缺文件/JSON 损坏/字段缺失一律返回默认 <see langword="true"/>。</summary>
    public static bool Load(string filePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("hideToTrayOnClose", out JsonElement v) &&
                   v.ValueKind == JsonValueKind.False
                ? false
                : true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 首启无文件是常态；损坏配置不阻断启动——回退默认保持历史行为
            return true;
        }
    }

    /// <summary>原子落盘（临时文件 + 改名），失败抛出由路由转错误帧。</summary>
    public static void Write(string filePath, bool hideOnClose)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmp = filePath + ".tmp";
        // 经 AppJsonContext 源生成（AOT 安全）；Load 读方只认 hideToTrayOnClose 键
        File.WriteAllText(tmp, JsonSerializer.Serialize(new PreferencesFile(hideOnClose), AppJsonContext.Default.PreferencesFile));
        File.Move(tmp, filePath, overwrite: true);
    }

    /// <summary>desktop-preferences.json 落盘帧；internal 供 <see cref="AppJsonContext"/> 源生成注册。</summary>
    /// <param name="HideToTrayOnClose">关闭按钮是否隐藏到托盘。</param>
    internal sealed record PreferencesFile(bool HideToTrayOnClose);
}
