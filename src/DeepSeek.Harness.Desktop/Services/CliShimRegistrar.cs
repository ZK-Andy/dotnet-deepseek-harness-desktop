using System.Runtime.InteropServices;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>一个待落盘的 shim 文件。</summary>
public sealed record CliShimFile(string TargetPath, string Content, bool Executable);

/// <summary>
/// CLI shim 注册的纯规划结果：要落盘的 shim 文件、bin 目录与 PATH 增量（Windows 为 HKCU 追加、
/// Unix 为 rc key 块）。
/// </summary>
public sealed record CliShimSetup(
    IReadOnlyList<CliShimFile> Files,
    string BinDir,
    string PathSeparator,
    string? ShellRcBlock);

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
/// CLI shim 注册纯逻辑（可单测）：把运行时 + DSH_HOME 烘焙进 shim，规划落盘文件与 PATH 增量，
/// 并判定写入动作（幂等合并、绝不覆盖用户配置）。平台 IO（注册表 / rc 落盘 / 广播）与
/// 编排见 <see cref="CliShimRegistrar"/>。
/// </summary>
public static class CliShimPlanner
{
    /// <summary>Windows shim 文件名。</summary>
    public const string DshCmdName = "dsh.cmd";
    public const string DshPs1Name = "dsh.ps1";
    public const string PnpmCmdName = "pnpm.cmd";
    public const string PnpmPs1Name = "pnpm.ps1";

    /// <summary>Unix shim 文件名。</summary>
    public const string DshShName = "dsh";
    public const string PnpmShName = "pnpm";

    /// <summary>按平台构造 shim 规划。<paramref name="writeDshShim"/> 为 false 时（dev 隔离）只写不烘焙
    /// home/hash 的 pnpm shim、跳过 dsh shim（对齐参照「debug 构建不写共享 dsh shim」，避免把开发环境
    /// 烘焙进用户终端；Windows 同样遵守，避免 dev 写成共享 %LOCALAPPDATA%）。</summary>
    public static CliShimSetup BuildSetup(
        string runtimeDir, string dshHome, string binDir, bool isWindows, bool writeDshShim)
    {
        var files = new List<CliShimFile>();
        if (isWindows)
        {
            if (writeDshShim)
            {
                files.Add(new CliShimFile(Path.Combine(binDir, DshCmdName), CliShimBuilder.BuildDshCmd(runtimeDir, dshHome), Executable: false));
                files.Add(new CliShimFile(Path.Combine(binDir, DshPs1Name), CliShimBuilder.BuildDshPs1(runtimeDir, dshHome), Executable: false));
            }

            files.Add(new CliShimFile(Path.Combine(binDir, PnpmCmdName), CliShimBuilder.BuildPnpmCmd(), Executable: false));
            files.Add(new CliShimFile(Path.Combine(binDir, PnpmPs1Name), CliShimBuilder.BuildPnpmPs1(), Executable: false));
            return new CliShimSetup(files, binDir, ";", ShellRcBlock: null);
        }

        if (writeDshShim)
        {
            files.Add(new CliShimFile(Path.Combine(binDir, DshShName), CliShimBuilder.BuildDshSh(runtimeDir, dshHome), Executable: true));
        }

        files.Add(new CliShimFile(Path.Combine(binDir, PnpmShName), CliShimBuilder.BuildPnpmSh(), Executable: true));
        return new CliShimSetup(files, binDir, ":", CliShimPath.BuildShellExportBlock(binDir, ":"));
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
        var fromEnv = Environment.GetEnvironmentVariable("DSH_DESKTOP_CLI_BIN_DIR");
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
    /// 执行一次 shim 注册。best-effort——吞掉预期内的异常并返回 false（调用方不因注册失败阻断启动）；
    /// 绝不抛给上层。调用方（Program.cs）在运行时就位且非 dev 隔离时调用。
    /// </summary>
    public bool TryRegister(string runtimeDir, string dshHome, bool isDevIsolated)
    {
        try
        {
            var binDir = ResolveBinDir();
            Directory.CreateDirectory(binDir);
            var setup = CliShimPlanner.BuildSetup(runtimeDir, dshHome, binDir, OperatingSystem.IsWindows(), writeDshShim: !isDevIsolated);
            foreach (var file in setup.Files)
            {
                WriteShimFile(file);
            }

            RegisterPath(setup);
            _log($"[cli-shim] 已注册终端命令到 {binDir}（dsh{(isDevIsolated ? "（dev 跳过），" : "/")}pnpm）");
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
        var target = file.TargetPath;
        var exists = File.Exists(target);
        var isSymlink = SymlinkTarget(target) is not null;
        var existing = exists ? SafeRead(target) : null;
        var isGenerated = CliShimPath.IsGeneratedShim(existing);

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
        var (current, expand) = ReadUserEnvPath();
        var merged = CliShimPath.MergePathToken(current, setup.BinDir, ";", caseInsensitive: true);
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

        foreach (var rc in ShellRcPaths())
        {
            if (rc is null)
            {
                continue;
            }

            var content = SafeRead(rc) ?? string.Empty;
            var updated = CliShimPath.EnsureShellRcBlock(content, setup.ShellRcBlock);
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
        var home = Environment.GetEnvironmentVariable("DSH_DESKTOP_CLI_RC_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        foreach (var name in new[] { ".bashrc", ".zshrc", ".profile", ".bash_profile", ".zprofile", ".zlogin" })
        {
            var path = Path.Combine(home, name);
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
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Environment");
        if (key is null)
        {
            return (string.Empty, true);
        }

        var raw = key.GetValue("Path", defaultValue: string.Empty, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        var expand = IsExpandString(key);
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
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Environment");
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
                HWND_BROADCAST,
                WM_SETTINGCHANGE,
                IntPtr.Zero,
                "Environment",
                SMTO_ABORTIFHUNG,
                new IntPtr(5000),
                out _);
        }
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
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

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
