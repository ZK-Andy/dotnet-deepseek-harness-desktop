using System.Text;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 开机自启（ADR shell-convenience-autostart-ready-notify）：Linux 走 XDG autostart
/// desktop entry、Windows 走 HKCU Run 键、macOS 走 LaunchAgents plist。
/// 条目文本构造器纯函数化可单测；可执行路径取 <see cref="Environment.ProcessPath"/>
/// （自更新原地升级不改路径，无需跟踪）。非敏感数据，不进诊断包白名单。
/// </summary>
public static class Autostart
{
    /// <summary>Linux entry 文件路径（XDG_CONFIG_HOME 未显式处理——桌面壳场景与上游同语义走默认）。</summary>
    public static string LinuxEntryPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "autostart", "deepseek-harness-desktop.desktop");

    /// <summary>macOS LaunchAgents plist 路径。</summary>
    public static string MacOSPlistPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", "io.github.zk-andy.dotnet-deepseek-harness-desktop.plist");

    /// <summary>构造 XDG autostart desktop entry 内容（纯函数，可单测）。</summary>
    public static string BuildLinuxDesktopEntry(string exePath) => $"""
        [Desktop Entry]
        Type=Application
        Name=DeepSeek Harness Desktop
        Exec={exePath}
        X-GNOME-Autostart-enabled=true
        """;

    /// <summary>构造 macOS LaunchAgent plist 内容（纯函数，可单测）。</summary>
    public static string BuildMacOSPlist(string exePath) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>io.github.zk-andy.dotnet-deepseek-harness-desktop</string>
          <key>ProgramArguments</key>
          <array>
            <string>{exePath}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
        </dict>
        </plist>
        """;

    /// <summary>查询当前是否启用（纯 IO 判定）。</summary>
    public static bool IsEnabled()
    {
        if (OperatingSystem.IsLinux())
        {
            return File.Exists(LinuxEntryPath());
        }

        if (OperatingSystem.IsMacOS())
        {
            return File.Exists(MacOSPlistPath());
        }

        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(AppName) is string;
        }

        return false;
    }

    /// <summary>启用/停用开机自启；返回生效后的状态。</summary>
    public static bool SetEnabled(bool enabled)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前可执行文件路径");

        if (!enabled)
        {
            Remove();
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            var path = LinuxEntryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildLinuxDesktopEntry(exe) + "\n");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var path = MacOSPlistPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildMacOSPlist(exe) + "\n");
        }
        else if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            throw new PlatformNotSupportedException("当前平台不支持开机自启");
        }

        return true;
    }

    private static void Remove()
    {
        if (OperatingSystem.IsLinux())
        {
            var path = LinuxEntryPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var path = MacOSPlistPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    private const string AppName = "DeepSeekHarnessDesktop";
}
