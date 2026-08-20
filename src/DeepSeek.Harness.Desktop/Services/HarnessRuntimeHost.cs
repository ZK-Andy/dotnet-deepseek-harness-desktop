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

    /// <summary>解析到的 dsh web UI URL；启动失败或超时为 null。</summary>
    public Uri? WebUrl { get; private set; }

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
    /// <remarks>首次用 OS 分配端口并记住；重启复用同端口，保证 WebView origin 不变（Web UI 的页面级会话记忆依赖同 origin）。</remarks>
    public async Task<Uri?> StartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Stop();
        WebUrl = null;
        Uri? url = await StartCoreAsync(_port, timeout, ct);
        if (url is null && _port is not null)
        {
            // 固定端口被占（kill 后未及时释放等）：回退 OS 分配
            url = await StartCoreAsync(null, timeout, ct);
        }

        WebUrl = url;
        if (url is not null)
        {
            _port = url.Port;
        }

        return WebUrl;
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

        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add("web");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port?.ToString() ?? "0");
        psi.Environment["DSH_HOME"] = home;

        // 桌面环境可能 /home 只读：把 pnpm store/cache 重定向到可写的 DSH_HOME 下，
        // 否则 dsh-market 等插件安装走 pnpm 会因 store 写入失败（EROFS）而失败。
        psi.Environment["pnpm_config_store_dir"] = Path.Combine(home, ".pnpm-store");
        psi.Environment["pnpm_config_cache_dir"] = Path.Combine(home, ".pnpm-cache");
        Directory.CreateDirectory(Path.Combine(home, ".pnpm-store"));
        Directory.CreateDirectory(Path.Combine(home, ".pnpm-cache"));

        // 桌面默认补丁：预装 dsh-market（已随包预装到 node_modules，首启 patch 无需联网，不阻塞 dsh web）
        var desktopPatch = Path.Combine(AppContext.BaseDirectory, "desktop.patch.yml");
        if (File.Exists(desktopPatch))
        {
            psi.ArgumentList.Add("--patch");
            psi.ArgumentList.Add(desktopPatch);
        }

        // 额外桌面覆盖层：DSH_DESKTOP_PATCH=<path> 时叠加（便于本机调试）
        var patchEnv = Environment.GetEnvironmentVariable("DSH_DESKTOP_PATCH");
        if (!string.IsNullOrWhiteSpace(patchEnv))
        {
            psi.ArgumentList.Add("--patch");
            psi.ArgumentList.Add(Path.GetFullPath(patchEnv));
        }

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
            WebUrl = await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            WebUrl = null;
        }

        return WebUrl;
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
