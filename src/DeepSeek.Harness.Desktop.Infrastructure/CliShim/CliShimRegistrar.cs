using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeepSeek.Harness.Desktop.Infrastructure.CliShim;

/// <summary>
/// CLI shim 注册编排：运行时就位后清理历史自产 <c>pnpm</c> 转发 shim 残留（ADR companion-pnpm-prerequisite
/// ——pnpm 由装配前置装真身，转发脚本已停写；残留会遮蔽真 pnpm），并把系统全局 node 的 bin 目录暴露进
/// 终端 PATH（Windows HKCU\Environment\Path + WM_SETTINGCHANGE 广播；Unix shell rc 幂等块）。
/// 全部 best-effort：任一步失败仅记日志告警，绝不阻断启动。dev 隔离（<see cref="DevEnvironment.IsDevRuntime"/>）
/// 时跳过注册。
/// </summary>
public sealed class CliShimRegistrar
{
    private readonly Action<string> _log;

    /// <summary>创建注册器。</summary>
    public CliShimRegistrar(Action<string> log) => _log = log;

    /// <summary>bin 目录（Windows <c>%LOCALAPPDATA%\deepseek-harness\bin</c>；Unix <c>~/.local/bin</c>；
    /// 经 <c>DSH_DESKTOP_CLI_BIN_DIR</c> 可覆盖，供 dev/测试隔离）。</summary>
    public static string ResolveBinDir()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("DSH_DESKTOP_CLI_BIN_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "deepseek-harness", "bin")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
    }

    /// <summary>
    /// 执行一次注册：清理历史自产 pnpm shim 残留 + 把系统全局 node bin 暴露进终端 PATH（仅当桌面装了
    /// 系统全局 node——用户自有 node 时终端本就能解析）。best-effort——吞掉预期内的异常并返回 false
    /// （调用方不因注册失败阻断启动）；绝不抛给上层。调用方（<c>FirstBootBootstrapService.RegisterCliShim</c>）
    /// 在启动时调用。
    /// </summary>
    public bool TryRegister(string? runtimeNodeBinDir = null)
    {
        try
        {
            string binDir = ResolveBinDir();
            // 不再建目录：撤写 shim 后 binDir 只用于残留扫描（对不存在路径 File.Exists/
            // LinkTarget 均安全返回），凭空创建空 ~/.local/bin 与「用户自管」相悖（R1 S1）。
            CliShimSetup setup = CliShimPlanner.BuildSetup(binDir, OperatingSystem.IsWindows(), runtimeNodeBinDir);
            int removed = RemoveLegacyPnpmShims(setup);
            RegisterPath(setup);
            string cleanup = removed > 0 ? $"；已清理历史 pnpm 转发 shim {removed} 个" : string.Empty;
            if (setup.RuntimeNodeBinDir is { } nodeBin)
            {
                _log($"[cli-shim] 终端 PATH 已暴露系统全局 node bin：{nodeBin}{cleanup}");
            }
            else
            {
                _log($"[cli-shim] 系统全局 node 为用户自有，无需 PATH 暴露{cleanup}");
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or PlatformNotSupportedException
            or System.ArgumentException
            or System.Security.SecurityException
            or System.NotSupportedException)
        {
            // 预期内失败（权限/平台/路径/注册表拒绝）：仅告警，绝不阻断启动
            _log($"[cli-shim] CLI shim 注册失败（不影响启动）：{ex.Message}");
            return false;
        }
    }

    /// <summary>清理历史自产 pnpm 转发 shim：仅删「内容带生成标记」或「悬空符号链接」的——
    /// 用户自放的同名文件（真 pnpm）永不触碰。</summary>
    private static int RemoveLegacyPnpmShims(CliShimSetup setup)
    {
        int removed = 0;
        foreach (string path in setup.LegacyShimPaths)
        {
            bool exists = File.Exists(path);
            bool isSymlink = SymlinkTarget(path) is not null;
            if (isSymlink && !exists)
            {
                // 悬空链接：目标已失效的无效物，无论出处直接清理
                File.Delete(path);
                removed++;
                continue;
            }

            if (CliShimPath.IsGeneratedShim(SafeRead(path)))
            {
                File.Delete(path);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>注册 PATH：Windows HKCU\Environment\Path 幂等追加 + WM_SETTINGCHANGE 广播；
    /// Unix shell rc 幂等块（已由 <paramref name="setup"/> 规划；无 node bin 可暴露时无块）。</summary>
    private static void RegisterPath(CliShimSetup setup)
    {
        if (OperatingSystem.IsWindows())
        {
            RegisterWindowsPath(setup);
        }
        else
        {
            RegisterShellRc(setup);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterWindowsPath(CliShimSetup setup)
    {
        if (setup.RuntimeNodeBinDir is not { } nodeBin)
        {
            return;
        }

        (string? current, bool expand) = ReadUserEnvPath();
        string merged = CliShimPath.MergePathToken(current, nodeBin, ";", caseInsensitive: true);
        if (string.Equals(current, merged, StringComparison.Ordinal))
        {
            return;
        }

        WriteUserEnvPath(merged, expand);
        BroadcastSettingChange();
    }

    private static void RegisterShellRc(CliShimSetup setup)
    {
        if (setup.ShellRcBlock is null)
        {
            return;
        }

        foreach (string? rc in ShellRcPaths())
        {
            if (rc is null)
            {
                continue;
            }

            string content = SafeRead(rc) ?? string.Empty;
            string updated = CliShimPath.EnsureShellRcBlock(content, setup.ShellRcBlock);
            if (!string.Equals(content, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(rc, updated);
            }
        }
    }

    /// <summary>Unix 目标 rc 文件（只写已存在的 shell 配置文件，不凭空创建——避免打扰用户；
    /// rc home 经 <c>DSH_DESKTOP_CLI_RC_HOME</c> 可覆盖，供测试隔离，默认用户主目录）。</summary>
    private static IEnumerable<string?> ShellRcPaths()
    {
        string? home = Environment.GetEnvironmentVariable("DSH_DESKTOP_CLI_RC_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        foreach (string? name in new[] { ".bashrc", ".zshrc", ".profile", ".bash_profile", ".zprofile", ".zlogin" })
        {
            string path = Path.Combine(home, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    // ------------------------------------------------------------------
    // Windows 注册表 / 环境广播（P/Invoke）
    // ------------------------------------------------------------------

    /// <summary>读取用户级 HKCU\Environment\Path 原始值（不展开 %VAR%）并记录其值类型，
    /// 避免把 <c>%USERPROFILE%\bin</c> 类字面量误判为「已有/未有」以及写回时改型。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string Value, bool Expand) ReadUserEnvPath()
    {
        using RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Environment");
        if (key is null)
        {
            return (string.Empty, true);
        }

        string raw = key.GetValue("Path", defaultValue: string.Empty, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        bool expand = IsExpandString(key);
        return (raw, expand);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsExpandString(Microsoft.Win32.RegistryKey key)
    {
        try
        {
            return key.GetValueKind("Path") == Microsoft.Win32.RegistryValueKind.ExpandString;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or System.ArgumentException)
        {
            // 「Path」值不存在或不可读：按默认可展开处理（写回不误改型）
            return true;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WriteUserEnvPath(string value, bool expand)
    {
        using RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Environment");
        key.SetValue("Path", value, expand
            ? Microsoft.Win32.RegistryValueKind.ExpandString
            : Microsoft.Win32.RegistryValueKind.String);
    }

    /// <summary>广播 WM_SETTINGCHANGE("Environment")，让 Explorer（已登录 shell）即时感知 PATH 变化。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void BroadcastSettingChange()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SendMessageTimeoutW(
                s_hwndBroadcast,
                WM_SETTINGCHANGE,
                IntPtr.Zero,
                "Environment",
                SMTO_ABORTIFHUNG,
                new IntPtr(5000),
                out _);
        }
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr s_hwndBroadcast = new(0xFFFF);
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, IntPtr timeout, out IntPtr result);

    // ------------------------------------------------------------------
    // 平台小工具
    // ------------------------------------------------------------------

    private static string? SymlinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 探不出的链上目标按「非符号链接」处理（清理决策退化为内容识别）
            return null;
        }
    }

    private static string? SafeRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
