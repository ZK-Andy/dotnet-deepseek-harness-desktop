using System.Diagnostics;
using System.Text;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>托管 dsh 运行时子进程：spawn `dsh --profile web --port 0`，解析 `dsh web:` URL，管理生命周期。</summary>
/// <remarks>对应 proposed architecture ADR：壳只负责运行时生命周期，组合的 Harness 插件树即应用运行时。</remarks>
public sealed class HarnessRuntimeHost : IDisposable
{
    private const int StderrTailCapacity = 40;

    private Process? _process;
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
    public async Task<Uri?> StartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Stop();
        WebUrl = null;

        var home = ResolveDshHome();
        Directory.CreateDirectory(home);

        var psi = new ProcessStartInfo
        {
            FileName = "dsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add("web");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add("0");
        psi.Environment["DSH_HOME"] = home;

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
