using System.Diagnostics;
using System.Text;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// <see cref="HarnessRuntimeHost"/> 的静态路径/环境解析面（partial，ADR 尺寸健康闸）。
/// 纯函数列：DSH_HOME 解析、profile/端口/PID 路径、pnpm 重定向、PATH 增补、进程流 UTF-8。
/// 生命周期与实例状态在 <c>HarnessRuntimeHost.cs</c>；本文件只承载无实例状态的静态单点。
/// </summary>
public sealed partial class HarnessRuntimeHost
{
    /// <summary>桌面专属覆盖环境变量：dev 自动隔离写入，也供用户显式指回旧私有 home；优先级最高。</summary>
    public const string HomeOverrideEnv = "DSH_DESKTOP_DSH_HOME";

    /// <summary>生态标准覆盖环境变量：与上游 CLI/TUI/Web 同语义（空白视为未设，支持 <c>~</c> 前缀）。</summary>
    public const string EcosystemHomeEnv = "DSH_HOME";

    /// <summary>上游规范 home 目录名（对齐上游 util/home-paths 的 <c>DSH_HOME_DIR_NAME</c>）。</summary>
    public const string DefaultHomeDirName = ".dsh";

    /// <summary>桌面专属 profile 名：启动组装与随包插件装配共用此单点，防两处漂移。</summary>
    internal const string DesktopProfileName = "desktop";

    private const string PortFileName = ".dsh-web-port";

    /// <summary>dsh 子进程 PID 记忆文件名（落于当前 profile 目录）：宿主异常死亡时 dsh 成孤儿被
    /// systemd 收养、继续占住首选端口（ADR self-update-exit-reaps-dsh-child，v0.3.11 实机
    /// PPID=systemd --user 实证）。下次冷启动据此清扫残留——跨平台，不全靠 Linux 的 PDEATHSIG。</summary>
    private const string PidFileName = ".dsh-pid";

    /// <summary>pnpm store 目录名（DSH_HOME 下）：desktop spawn 的 dsh 子进程被注入
    /// <c>pnpm_config_store_dir</c> 指向此目录（<see cref="ApplyPnpmWriteDirs"/>），CLI shim 注册时
    /// 也把它导出进 shell rc（<see cref="CliShimRegistrar.ResolvePnpmDirs"/>）——两处共用此单点防漂移
    /// （ADR pnpm-store-alignment-with-terminal）。</summary>
    internal const string PnpmStoreDirName = ".pnpm-store";

    /// <summary>pnpm cache 目录名（DSH_HOME 下）；语义同 <see cref="PnpmStoreDirName"/>。</summary>
    internal const string PnpmCacheDirName = ".pnpm-cache";

    /// <summary>
    /// 子进程文本流显式 UTF-8（单点）：dsh/npm/node 系子进程输出恒 UTF-8，不显式声明时
    /// Windows 按系统 OEM 码页（如 GBK）解码，中文日志进 stderr tail/诊断包变乱码
    /// （.NET replacement fallback 不炸管道，但观测面全花；竞品 #197 崩溃类的 .NET 变体，
    /// ADR online-first-unbundled-runtime 踩坑约束）。
    /// </summary>
    internal static void UseUtf8TextStreams(ProcessStartInfo psi)
    {
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
    }

    /// <summary>上次 spawn 的 dsh PID 文件路径（按 profile 隔离，同端口文件）。</summary>
    internal static string ResolvePidFilePath() =>
        Path.Combine(ResolveDshHome(), "profiles", DesktopProfileName, PidFileName);

    /// <summary>把 pnpm store/cache 重定向到 <paramref name="home"/> 下并预建目录（dsh spawn 与随包插件安装两条链共用的单点）。</summary>
    /// <remarks>
    /// 桌面环境可能 /home 只读：不重定向时 dsh-market 等插件安装走 pnpm 会因 store 写入失败（EROFS）而失败；
    /// 预建目录兼容旧 pnpm 的 store 仍被读取时不因 EROFS 失败。
    /// </remarks>
    internal static void ApplyPnpmWriteDirs(ProcessStartInfo psi, string home)
    {
        psi.Environment["pnpm_config_store_dir"] = Path.Combine(home, PnpmStoreDirName);
        psi.Environment["pnpm_config_cache_dir"] = Path.Combine(home, PnpmCacheDirName);
        Directory.CreateDirectory(Path.Combine(home, PnpmStoreDirName));
        Directory.CreateDirectory(Path.Combine(home, PnpmCacheDirName));
    }

    /// <summary>记录本次 spawn 的 dsh PID + 孤儿清扫 token（尽力而为：写失败仅导致下次冷启动清扫落空，端口漂移告警兜底）。</summary>
    internal static void PersistSpawn(int pid, string token)
    {
        try
        {
            string path = ResolvePidFilePath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, $"{pid}\n{token}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostLog.Write($"[host] 写 dsh PID 失败（下次冷启动清扫将落空）：{ex.Message}");
        }
    }

    /// <summary>端口状态文件路径（落于当前 profile 目录）。桌面端与 web 会话共享同一 DSH_HOME，
    /// home 根的全局端口记忆会让两类 dsh 实例互相抢占端口——v0.3.5 实机事故：自更新拉起后
    /// 与 web 会话在同一端口互顶，恢复屏循环直至用户重启电脑。按 profile 隔离后各记各的端口。</summary>
    internal static string ResolvePortFilePath() =>
        Path.Combine(ResolveDshHome(), "profiles", DesktopProfileName, PortFileName);

    /// <summary>旧版端口记忆位置（home 根）：仅作迁移回读，不再写入。</summary>
    internal static string ResolveLegacyPortFilePath() => Path.Combine(ResolveDshHome(), PortFileName);

    /// <summary>读取上次成功端口：跨 App 冷启动复用同端口 → WebView origin 不变 → dsh Web 端"当前会话"localStorage
    /// （<c>dsh.sessions.current</c>，按 origin 隔离）仍命中 → 恢复上次会话。新位置缺失时回读旧版 home 根文件
    /// （存量升级零感知）；两处均缺失/损坏/不可读 → null（回退 OS 分配）。</summary>
    internal static int? TryLoadPersistedPort()
    {
        string newPath = ResolvePortFilePath();
        if (File.Exists(newPath))
        {
            // 新位置存在（含损坏/不可读）：不回读旧版——避免陈旧的 home 根端口记忆劫持当前 profile
            return TryReadPortFile(newPath);
        }

        return TryReadPortFile(ResolveLegacyPortFilePath());
    }

    private static int? TryReadPortFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string text = File.ReadAllText(path).Trim();
            return int.TryParse(text, out int port) && port is > 0 and <= 65535 ? port : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // DSH_HOME 暂不可读：回退 OS 分配端口（fail loud 由下方端口占位回退兜底）
            HostLog.Write($"[host] 读取上次端口失败（将回退 OS 分配）：{ex.Message}");
            return null;
        }
    }

    /// <summary>持久化最近一次成功端口（尽力而为；写失败仅导致下次冷启动换端口→新会话，不阻断本次运行，故不 fail loud）。
    /// 只写当前 profile 路径——绝不回写旧版 home 根文件，避免跨 profile 争抢延续。</summary>
    internal static void PersistPort(int port)
    {
        try
        {
            string path = ResolvePortFilePath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, port.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写失败仅导致下次冷启动换端口→新会话，不阻断本次运行，故不 fail loud
            HostLog.Write($"[host] 写端口状态失败（下次冷启动将换端口）：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析共享 DSH_HOME（B 形态，ADR shared-home-desktop-profile）。优先级：桌面专属覆盖
    /// <see cref="HomeOverrideEnv"/>（dev 隔离 / 用户显式回退）→ 生态标准 <see cref="EcosystemHomeEnv"/>
    /// （与上游 CLI/TUI/Web 同语义：空白视为未设、支持 <c>~</c> 前缀）→ 上游规范 home
    /// <c>~/.dsh</c>。home 层数据（sessions/credentials/workspaces）由此与生态其他前端天然互通。
    /// </summary>
    public static string ResolveDshHome()
    {
        string? desktop = Environment.GetEnvironmentVariable(HomeOverrideEnv);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            return Path.GetFullPath(ExpandHome(desktop));
        }

        string? ecosystem = Environment.GetEnvironmentVariable(EcosystemHomeEnv);
        if (!string.IsNullOrWhiteSpace(ecosystem))
        {
            return Path.GetFullPath(ExpandHome(ecosystem));
        }

        return DefaultDshHome();
    }

    /// <summary>上游规范默认 home <c>~/.dsh</c>（对齐上游 home-paths 的 <c>defaultDshHome</c>）。</summary>
    private static string DefaultDshHome() =>
        Path.Combine(UserHome, DefaultHomeDirName);

    private static string UserHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>展开 <c>~</c> 前缀（<c>~</c>、<c>~/</c>、<c>~\</c>）到用户主目录；其余原样返回。对齐上游 expandHomePath。</summary>
    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return UserHome;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(UserHome, path[2..]);
        }

        return path;
    }

    /// <summary>构造 dsh web 参数。含 <c>--no-open</c>：桌面壳把返回的 <c>dsh web:</c> URL
    /// 渲染进内嵌 WebView 即可，若把它交给 dsh 默认行为（rc.8+ <c>openBrowser</c> 默认开）
    /// 会额外弹出 OS 默认浏览器，与桌面窗口重复。</summary>
    /// <param name="port">固定端口；<c>null</c> 时让 OS 分配（<c>--port 0</c>）。</param>
    internal static string[] BuildDshWebArgs(int? port) => new[]
    {
        "--profile",
        DesktopProfileName,
        "--port",
        port?.ToString() ?? "0",
        "--no-open",
    };

    /// <summary>构造子进程 PATH：把 <c>$HOME/.local/bin</c> 追加到现有 PATH 之后（缺则追加、已含幂等）。</summary>
    /// <remarks>
    /// GUI 会话应用继承的 PATH 是 systemd 用户管理器的默认精简值，不含用户级 bin 目录，
    /// 而 dsh 内的 MCP stdio 等下游工具按命令名拉取外部进程时依赖它。追加而非前置：
    /// 不改变系统命令的解析优先级（ADR gui-path-enrichment）。
    /// </remarks>
    internal static string BuildEnrichedPath(string? currentPath, string home, char separator)
    {
        string localBin = Path.Combine(home, ".local", "bin");
        string existing = currentPath ?? string.Empty;
        if (existing.Split(separator).Contains(localBin))
        {
            return existing;
        }

        return existing.Length == 0 ? localBin : existing + separator + localBin;
    }
}
