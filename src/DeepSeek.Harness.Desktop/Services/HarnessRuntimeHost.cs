using System.Diagnostics;
using System.Text;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>托管 dsh 运行时子进程：spawn dsh（`--profile desktop --port 0`），解析 `dsh web:` URL，管理生命周期。</summary>
/// <remarks>对应 implemented architecture ADR shared-home-desktop-profile：壳只负责运行时生命周期，组合的 Harness
/// 插件树即应用运行时；产品态默认上游规范共享 home `~/.dsh`，专属 `profiles/desktop` 承载插件装配。
/// 支持捆绑运行时（<paramref name="bundled"/> 指定 node + dsh bin.js）；为 null 时回退 PATH 里的 dsh。</remarks>
public sealed class HarnessRuntimeHost : IDisposable
{
    private const int StderrTailCapacity = 40;

    /// <summary>桌面专属覆盖环境变量：dev 自动隔离写入，也供用户显式指回旧私有 home；优先级最高。</summary>
    public const string HomeOverrideEnv = "DSH_DESKTOP_DSH_HOME";

    /// <summary>生态标准覆盖环境变量：与上游 CLI/TUI/Web 同语义（空白视为未设，支持 <c>~</c> 前缀）。</summary>
    public const string EcosystemHomeEnv = "DSH_HOME";

    /// <summary>上游规范 home 目录名（对齐上游 util/home-paths 的 <c>DSH_HOME_DIR_NAME</c>）。</summary>
    public const string DefaultHomeDirName = ".dsh";

    /// <summary>桌面专属 profile 名：启动组装与随包插件装配共用此单点，防两处漂移。</summary>
    internal const string DesktopProfileName = "desktop";

    private readonly (string NodeExe, string DshEntry)? _bundled;
    private int? _port;
    private Process? _process;

    /// <summary>创建运行时宿主。</summary>
    /// <param name="bundled">捆绑运行时 (node 可执行, dsh bin.js)；null 表示用 PATH 的 dsh。</param>
    public HarnessRuntimeHost((string NodeExe, string DshEntry)? bundled = null)
    {
        _bundled = bundled;
    }

    /// <summary>本次采用的运行时描述（日志/恢复屏用）。</summary>
    public string RuntimeDescription =>
        _bundled is { } b ? $"bundled node={b.NodeExe} bin={b.DshEntry}" : "PATH dsh";
    private readonly List<string> _stderrTail = new(StderrTailCapacity);
    private readonly object _stderrLock = new();

    private const string PortFileName = ".dsh-web-port";

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
        var port = TryReadPortFile(ResolvePortFilePath());
        if (port is null && !File.Exists(ResolvePortFilePath()))
        {
            port = TryReadPortFile(ResolveLegacyPortFilePath());
        }

        return port;
    }

    private static int? TryReadPortFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return int.TryParse(text, out var port) && port is > 0 and <= 65535 ? port : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // DSH_HOME 暂不可读：回退 OS 分配端口（fail loud 由下方端口占位回退兜底）
            Console.WriteLine($"[host] 读取上次端口失败（将回退 OS 分配）：{ex.Message}");
            return null;
        }
    }

    /// <summary>持久化最近一次成功端口（尽力而为；写失败仅导致下次冷启动换端口→新会话，不阻断本次运行，故不 fail loud）。
    /// 只写当前 profile 路径——绝不回写旧版 home 根文件，避免跨 profile 争抢延续。</summary>
    internal static void PersistPort(int port)
    {
        try
        {
            var path = ResolvePortFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, port.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[host] 写端口状态失败（下次冷启动将换端口）：{ex.Message}");
        }
    }

    /// <summary>失败时可读的诊断尾巴（stderr 末 N 行）。</summary>
    public IReadOnlyList<string> StderrTail
    {
        get
        {
            lock (_stderrLock)
            {
                return _stderrTail.ToArray();
            }
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
        var desktop = Environment.GetEnvironmentVariable(HomeOverrideEnv);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            return Path.GetFullPath(ExpandHome(desktop));
        }

        var ecosystem = Environment.GetEnvironmentVariable(EcosystemHomeEnv);
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

    /// <summary>启动 dsh web（OS 分配端口），等待 `dsh web:` URL 或超时。</summary>
    /// <param name="timeout">等待 URL 的时限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>dsh web UI 的 URL；未在时限内给出则为 null。</returns>
    /// <remarks>端口需跨 App 冷启动保持稳定：origin 不变 → dsh Web 端"当前会话"localStorage（dsh.sessions.current，按 origin 隔离）
    /// 仍命中 → 恢复上一会话。进程内崩溃重启复用 <paramref name="ct"/> 前记忆的 <c>_port</c>；冷启动从磁盘加载上次端口并回写。</remarks>
    public async Task<Uri?> StartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Stop();
        var preferred = _port ?? TryLoadPersistedPort();
        Uri? url = await StartCoreAsync(preferred, timeout, ct);
        if (url is null && preferred is not null)
        {
            // 固定端口被占（kill 后未及时释放 / 其他进程占用）：回退 OS 分配
            url = await StartCoreAsync(null, timeout, ct);
        }

        if (url is not null)
        {
            _port = url.Port;
            // 跨进程持久化：冷启动复用同端口（origin 不变）才能恢复 dsh Web 端的上一会话
            PersistPort(url.Port);
        }

        return url;
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
        var localBin = Path.Combine(home, ".local", "bin");
        var existing = currentPath ?? string.Empty;
        if (existing.Split(separator).Contains(localBin))
        {
            return existing;
        }

        return existing.Length == 0 ? localBin : existing + separator + localBin;
    }

    private async Task<Uri?> StartCoreAsync(int? port, TimeSpan timeout, CancellationToken ct = default)
    {
        var home = ResolveDshHome();
        Directory.CreateDirectory(home);

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        if (_bundled is { } b)
        {
            psi.FileName = b.NodeExe;
            psi.ArgumentList.Add(b.DshEntry);
        }
        else
        {
            psi.FileName = "dsh";
        }

        foreach (var arg in BuildDshWebArgs(port))
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["DSH_HOME"] = home;

        // 桌面环境可能 /home 只读：把 pnpm store/cache 重定向到可写的 DSH_HOME 下，
        // 否则 dsh-market 等插件安装走 pnpm 会因 store 写入失败（EROFS）而失败。
        psi.Environment["pnpm_config_store_dir"] = Path.Combine(home, ".pnpm-store");
        psi.Environment["pnpm_config_cache_dir"] = Path.Combine(home, ".pnpm-cache");
        Directory.CreateDirectory(Path.Combine(home, ".pnpm-store"));
        Directory.CreateDirectory(Path.Combine(home, ".pnpm-cache"));

        // GUI 会话的 PATH 不含 ~/.local/bin：MCP stdio 等下游按命令名拉取外部进程时
        // 解析不到用户级命令（ADR gui-path-enrichment）。追加而非前置，不改系统优先级。
        psi.Environment["PATH"] = BuildEnrichedPath(
            psi.Environment.TryGetValue("PATH", out var currentPath) ? currentPath : null,
            home,
            Path.PathSeparator);

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 dsh 进程。");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_stderrLock)
            {
                _stderrTail.Add(e.Data);
                if (_stderrTail.Count > StderrTailCapacity)
                {
                    _stderrTail.RemoveRange(0, _stderrTail.Count - StderrTailCapacity);
                }
            }
        };
        _process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var tcs = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            var uri = HarnessUrlParser.TryParse(e.Data);
            if (uri is not null)
            {
                tcs.TrySetResult(uri);
            }
        };
        _process.BeginOutputReadLine();

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>重启 dsh 子进程（先 Stop 再 StartAsync），返回新 URL。崩溃恢复用。</summary>
    /// <param name="timeout">等待新 URL 的时限。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Uri?> RestartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Stop();
        return await StartAsync(timeout, ct);
    }

    /// <summary>当 dsh 子进程退出时完成（用于崩溃监督；子进程不存在时立即完成）。</summary>
    public Task WaitForExitAsync()
    {
        if (_process is not { HasExited: false } p)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    /// <summary>停止并回收 dsh 子进程（整棵进程树）。</summary>
    public void Stop()
    {
        if (_process is { HasExited: false } p)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程恰好已退出
            }

            p.WaitForExit(3000);
        }

        _process = null;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
