using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>一个待落盘的 shim 文件。</summary>
public sealed record CliShimFile(string TargetPath, string Content, bool Executable);

/// <summary>
/// CLI shim 注册的纯规划结果：要落盘的 shim 文件、bin 目录与 PATH 增量（Windows 为 HKCU 追加、
/// Unix 为 rc key 块）。<paramref name="RuntimeNodeBinDir"/> 为系统全局 node 的 global bin 目录
/// （无系统 node 时由桌面装到系统全局；把它一并暴露进终端 PATH，让桌面与终端共用同一份 node/dsh）。
/// </summary>
public sealed record CliShimSetup(
    IReadOnlyList<CliShimFile> Files,
    string BinDir,
    string PathSeparator,
    string? ShellRcBlock,
    string? RuntimeNodeBinDir = null);

/// <summary>写入 shim 目标的决策。</summary>
public enum ShimWriteAction
{
    /// <summary>目标不存在，或为本应用生成的 shim：直接写入。</summary>
    Write,

    /// <summary>目标为用户自行放置的同名文件：保留。</summary>
    PreserveUserFile,

    /// <summary>目标为悬空符号链接（目标已失效）：先移除链接再写入。</summary>
    RemoveDanglingSymlinkThenWrite,
}

/// <summary>
/// CLI shim 注册纯逻辑（可单测）：规划 <c>pnpm</c> shim 落盘文件与 PATH 增量，
/// 并判定写入动作（幂等合并、绝不覆盖用户配置）。dsh 已全局在 PATH（ADR simple-shell-single-global-dsh），
/// 不再生成 dsh shim。平台 IO（注册表 / rc 落盘 / 广播）与编排见 <see cref="CliShimRegistrar"/>。
/// </summary>
public static class CliShimPlanner
{
    /// <summary>Windows shim 文件名（pnpm 命令）。</summary>
    public const string PnpmCmdName = "pnpm.cmd";
    /// <summary>Windows PowerShell shim 文件名（pnpm 命令）。</summary>
    public const string PnpmPs1Name = "pnpm.ps1";

    /// <summary>Unix shim 文件名（pnpm 命令）。</summary>
    public const string PnpmShName = "pnpm";

    /// <summary>按平台构造 shim 规划（仅 pnpm；dsh 已全局在 PATH，内容恒定，不烘焙运行时/DSH_HOME）。
    /// <paramref name="runtimeNodeBinDir"/> 非空时（系统全局 node 由桌面装好）一并把它暴露进终端 PATH。</summary>
    public static CliShimSetup BuildSetup(string binDir, bool isWindows, string? runtimeNodeBinDir = null)
    {
        var files = new List<CliShimFile>();
        // 系统全局 node 的 bin 目录前置：终端优先用桌面与终端共用那份 node/dsh（无系统 node 场景）。
        string[] pathDirs = string.IsNullOrWhiteSpace(runtimeNodeBinDir)
            ? new[] { binDir }
            : new[] { runtimeNodeBinDir, binDir };
        if (isWindows)
        {
            files.Add(new CliShimFile(Path.Combine(binDir, PnpmCmdName), CliShimBuilder.BuildPnpmCmd(), Executable: false));
            files.Add(new CliShimFile(Path.Combine(binDir, PnpmPs1Name), CliShimBuilder.BuildPnpmPs1(), Executable: false));
            return new CliShimSetup(files, binDir, ";", ShellRcBlock: null, RuntimeNodeBinDir: runtimeNodeBinDir);
        }

        files.Add(new CliShimFile(Path.Combine(binDir, PnpmShName), CliShimBuilder.BuildPnpmSh(), Executable: true));
        return new CliShimSetup(files, binDir, ":", CliShimPath.BuildShellExportBlocks(pathDirs, ":"), RuntimeNodeBinDir: runtimeNodeBinDir);
    }

    /// <summary>判定写入动作：悬空符号链接先移除；本应用生成/不存在则写；用户文件保留。
    /// 悬空 = 链接本身存在但目标已被移动/删除（<c>File.Exists</c> 跟随链接故为 false）。</summary>
    public static ShimWriteAction DecideShimWrite(bool exists, bool isGeneratedShim, bool isSymlink)
    {
        if (isSymlink && !exists)
        {
            return ShimWriteAction.RemoveDanglingSymlinkThenWrite;
        }

        if (exists && !isGeneratedShim)
        {
            return ShimWriteAction.PreserveUserFile;
        }

        return ShimWriteAction.Write;
    }
}

/// <summary>
/// CLI shim 注册编排：运行时就位后把 <c>dsh</c>/<c>pnpm</c> shim 落盘到用户 bin 目录并注册进
/// PATH（Windows HKCU\Environment\Path + WM_SETTINGCHANGE 广播；Unix ~/.local/bin + shell rc
/// 幂等块）。全部 best-effort：任一步失败仅记日志告警，绝不阻断启动（ADR reference-alignment
/// 批次四）。dev 隔离（<see cref="DevEnvironment.IsDevRuntime"/>）时跳过 dsh shim 注册。
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
    /// 执行一次 shim 注册（仅 pnpm；dsh 已全局在 PATH，无需生成 dsh shim）。<paramref name="runtimeNodeBinDir"/>
    /// 非空时（系统全局 node 由桌面装好）一并把它暴露进终端 PATH，让终端与桌面共用同一份 node/dsh。
    /// best-effort——吞掉预期内的异常并返回 false（调用方不因注册失败阻断启动）；绝不抛给上层。
    /// 调用方（<see cref="DesktopBootstrap.Startup"/> 的 RegisterCliShim）在启动时调用。
    /// </summary>
    public bool TryRegister(string? runtimeNodeBinDir = null)
    {
        try
        {
            string binDir = ResolveBinDir();
            Directory.CreateDirectory(binDir);
            CliShimSetup setup = CliShimPlanner.BuildSetup(binDir, OperatingSystem.IsWindows(), runtimeNodeBinDir);
            foreach (CliShimFile file in setup.Files)
            {
                WriteShimFile(file);
            }

            RegisterPath(setup);
            _log($"[cli-shim] 已注册终端命令到 {binDir}（pnpm{(setup.RuntimeNodeBinDir is null ? "" : $"；系统全局 node = {setup.RuntimeNodeBinDir}")}）");
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

    /// <summary>写单个 shim 文件：悬空符号链接先移除；本应用生成/不存在则写；用户文件保留。</summary>
    private static void WriteShimFile(CliShimFile file)
    {
        string target = file.TargetPath;
        bool exists = File.Exists(target);
        bool isSymlink = SymlinkTarget(target) is not null;
        string? existing = exists ? SafeRead(target) : null;
        bool isGenerated = CliShimPath.IsGeneratedShim(existing);

        switch (CliShimPlanner.DecideShimWrite(exists, isGenerated, isSymlink))
        {
            case ShimWriteAction.PreserveUserFile:
                // 用户手动放置的同名 dsh/pnpm：保留，绝不覆盖
                return;
            case ShimWriteAction.RemoveDanglingSymlinkThenWrite:
                File.Delete(target);
                break;
        }

        File.WriteAllText(target, file.Content);
        if (file.Executable)
        {
            MakeExecutable(target);
        }
    }

    /// <summary>注册 PATH：Windows HKCU\Environment\Path 幂等追加 + WM_SETTINGCHANGE 广播；
    /// Unix ~/.local/bin + shell rc 幂等块（已由 <paramref name="setup"/> 规划）。</summary>
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
        (string? current, bool expand) = ReadUserEnvPath();
        string merged = CliShimPath.MergePathToken(current, setup.BinDir, ";", caseInsensitive: true);
        if (setup.RuntimeNodeBinDir is not null)
        {
            merged = CliShimPath.MergePathToken(merged, setup.RuntimeNodeBinDir, ";", caseInsensitive: true);
        }

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
            // 探不出的链上目标按「非符号链接」处理（写入决策退化为普通文件存在性判断）
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

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
