using System.Diagnostics;
using System.Text;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>托管 dsh 运行时子进程：spawn dsh web（`--profile web --port 0`），解析 `dsh web:` URL，管理生命周期。</summary>
/// <remarks>对应 proposed architecture ADR：壳只负责运行时生命周期，组合的 Harness 插件树即应用运行时。
/// 支持捆绑运行时（<paramref name="bundled"/> 指定 node + dsh bin.js）；为 null 时回退 PATH 里的 dsh。</remarks>
public sealed class HarnessRuntimeHost : IDisposable
{
    private const int StderrTailCapacity = 40;

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

    /// <summary>端口状态文件路径（落于 DSH_HOME 下，随 LocalApplicationData 承载、可写；与测试的环境覆盖一致）。</summary>
    internal static string ResolvePortFilePath() => Path.Combine(ResolveDshHome(), PortFileName);

    /// <summary>读取上次成功端口：跨 App 冷启动复用同端口 → WebView origin 不变 → dsh Web 端"当前会话"localStorage
    /// （<c>dsh.sessions.current</c>，按 origin 隔离）仍命中 → 恢复上次会话。文件缺失/损坏/不可读 → null（回退 OS 分配）。</summary>
    internal static int? TryLoadPersistedPort()
    {
        try
        {
            var path = ResolvePortFilePath();
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

    /// <summary>持久化最近一次成功端口（尽力而为；写失败仅导致下次冷启动换端口→新会话，不阻断本次运行，故不 fail loud）。</summary>
    internal static void PersistPort(int port)
    {
        try
        {
            Directory.CreateDirectory(ResolveDshHome());
            File.WriteAllText(ResolvePortFilePath(), port.ToString());
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

    /// <summary>解析 DSH_HOME：优先环境变量 <c>DSH_DESKTOP_DSH_HOME</c>（开发/测试覆盖），否则本地应用数据目录。</summary>
    public static string ResolveDshHome()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DSH_DESKTOP_DSH_HOME");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeek.Harness.Desktop",
            "dsh");
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
        "web",
        "--port",
        port?.ToString() ?? "0",
        "--no-open",
    };

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
