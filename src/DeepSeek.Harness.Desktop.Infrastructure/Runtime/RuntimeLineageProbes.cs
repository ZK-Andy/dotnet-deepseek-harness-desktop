using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// <see cref="RuntimeLineage"/> 策略核（Core）的生产探针面：<c>/proc</c> 读取、进程树击杀、回环端口探活。
/// 判定逻辑（分类/树内排除/处置计划）留在 <c>RuntimeLineage.cs</c>，本文件只做外部世界交互，
/// 故列在 D005 边界白名单（verify-code-conventions）。
/// </summary>
public static class RuntimeLineageProbes
{
    /// <summary>端口探活的单次超时（TCP 连接本机回环：连不上即无人监听）。</summary>
    private static readonly TimeSpan s_portProbeTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>web 面就绪探针的单次超时（HTTP 要等后端应答，比 TCP 探活留更宽余量）。</summary>
    private static readonly TimeSpan s_webProbeTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>web 面就绪探针的复用客户端：不跟随重定向（3xx 本身就是「已应答」判据）、环回不走代理；
    /// 超时交每次探测自己的取消令牌（<c>Timeout.InfiniteTimeSpan</c> 防两处超时打架）。</summary>
    private static readonly HttpClient s_webProbeClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>枚举当前所有携带血统 token 的进程快照（Linux <c>/proc</c>；其他平台或读不到环境一律返回空）。</summary>
    /// <returns>候选快照列表；空表示无血统进程（也包含「平台不支持读取」这一保守退化）。</returns>
    /// <remarks>逐 pid 读 <c>/proc/&lt;pid&gt;/environ</c> 是唯一能发现血统进程的手段：先做 token 子串粗筛，
    /// 命中才解析 env 与命令行；读不到（他人进程/已退出）静默跳过，绝不因此误判为血统。</remarks>
    public static IReadOnlyList<RuntimeLineage.Candidate> Enumerate()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Windows/macOS 无统一的进程环境读取：保守返回空（清扫退回 .dsh-pid 记录路径）
            return [];
        }

        var candidates = new List<RuntimeLineage.Candidate>();
        try
        {
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out int pid))
                {
                    continue;
                }

                RuntimeLineage.Candidate? candidate = TryReadCandidate(pid);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // /proc 不可遍历（受限容器）：本次扫描按无残留处理——观测位（漂移告警）兜底
            HostLog.Write($"[host] 血统扫描无法遍历 /proc（跳过）：{ex.Message}");
        }

        return candidates;
    }

    /// <summary>读某进程 env 里的血统 token（<see cref="RuntimeLineage.TokenEnv"/>；旧名回退见
    /// <see cref="RuntimeLineage.LegacyTokenEnv"/>）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>token；进程已死/无权限/非 Linux 返回 null（调用方按「不匹配」处理，零误杀）。</returns>
    public static string? ReadToken(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return ReadTokenFromEnviron(File.ReadAllText($"/proc/{pid}/environ"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 进程已死/无权限读：读不到 token → 不杀（零误杀）
            return null;
        }
    }

    /// <summary>从 environ 原文取血统 token（纯函数）：先新名，取不到再回退旧名——升级窗口内上一版 dsh 的
    /// <c>.dsh-pid</c> 记录只带旧名，只读新名会让它「复验不匹配」而被跳过，孤儿继续占住首选端口。</summary>
    /// <param name="environ">NUL 分隔的 environ 原文。</param>
    /// <returns>token；两处都没有（或都是空值）返回 null（调用方按不匹配处理）。</returns>
    internal static string? ReadTokenFromEnviron(string environ) =>
        NonEmpty(EnvValue(environ, RuntimeLineage.TokenEnv))
        ?? NonEmpty(EnvValue(environ, RuntimeLineage.LegacyTokenEnv));

    /// <summary>空值折算为「不存在」——否则一个空串新名会遮蔽有效的旧名（零误杀方向要求「读不到即不匹配」）。</summary>
    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>读某进程的父进程 id（<c>/proc/&lt;pid&gt;/stat</c> 第 4 字段，comm 含空格故从最后一个 ')' 起切）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>父 pid；读不到返回 null（调用方按父链断裂处理）。</returns>
    public static int? ReadParentPid(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int close = stat.LastIndexOf(')');
            if (close < 0)
            {
                return null;
            }

            string[] fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // fields[0] = state，fields[1] = ppid
            return fields.Length > 1 && int.TryParse(fields[1], out int parentPid) ? parentPid : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>整树击杀某 pid（残留收割与在管运行时回收共用；.NET 在 Unix 上即 SIGKILL）。</summary>
    /// <param name="pid">目标进程 id。</param>
    public static void KillTree(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
    }

    /// <summary>判定某 pid 是否仍在运行（收养的运行时的判活手段——它非本进程子进程，挂不上 Exited 事件）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>仍在运行返回 true；不存在或无法判定返回 false（走恢复路径）。</returns>
    /// <remarks>Linux 用 <c>/proc/&lt;pid&gt;</c> 存在性判定；其他平台退化为 <see cref="Process"/> 句柄判定。
    /// 当前调用图里只有 Linux 收养路径可达（<see cref="Enumerate"/> 非 Linux 恒空 ⇒ 不收养），非 Linux 分支
    /// 作为边界组件 API 的跨平台完备性保留。</remarks>
    public static bool TryIsAlive(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            return Directory.Exists($"/proc/{pid}");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>探测本机回环某端口此刻是否有监听者（TCP 连接成功即视为有人服务）。</summary>
    /// <remarks>只证「有人监听」：dsh 先 bind 端口、web 面（认证 + 前端静态）由更晚挂载的行提供，
    /// 「可导航」的就绪判定见 <see cref="ProbeLoopbackWebAsync"/>（ADR relay-web-readiness）。</remarks>
    /// <param name="port">端口。</param>
    /// <param name="timeout">单次探测超时；传 null 用 <see cref="s_portProbeTimeout"/>。</param>
    /// <param name="ct">取消令牌：调用方取消即立即返回（不额外等待完探测超时）。</param>
    /// <returns>有人监听返回 true。</returns>
    public static async Task<bool> IsLoopbackServingAsync(int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port)
                .WaitAsync(timeout ?? s_portProbeTimeout, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>回环 web 面就绪三态（ADR relay-web-readiness）。</summary>
    public enum LoopbackWebProbe
    {
        /// <summary>端口无人监听（TCP 连接失败）：续任者尚未 bind。</summary>
        NotServing,

        /// <summary>端口已监听但 HTTP 应答无声（空体/超时）：web 面（认证 + 前端静态，由更晚挂载的行
        /// 提供）尚未就绪。此时导航只会把 WebView 送进空白页。</summary>
        ServingNotReady,

        /// <summary>web 面已应答（响应带响应体，或 3xx 重定向）：可导航。</summary>
        Ready,
    }

    /// <summary>探测本机回环端口此刻的 web 面就绪态：TCP 可连只证明 dsh 已 bind，「可导航」另需 web 面已应答
    /// （ADR relay-web-readiness——dsh 先 bind、web-runtime 行后挂载，两者之间有一段只监听不应答的窗口）。</summary>
    /// <param name="port">端口。</param>
    /// <param name="ct">取消令牌（调用方取消即按未就绪返回，上层循环立刻回落）。</param>
    /// <param name="timeout">可用预算上界：实际单次等待取它与 <see cref="s_webProbeTimeout"/> 的较小者，
    /// 供调用方按剩余预算收紧（每轮迭代总耗时因此有界）；传 null 即用默认值。</param>
    /// <returns>就绪三态；探测失败一律折算为「未就绪」语义，绝不因探测异常判死。</returns>
    public static async Task<LoopbackWebProbe> ProbeLoopbackWebAsync(int port, CancellationToken ct, TimeSpan? timeout = null)
    {
        // 整次探测（TCP 预检 + HTTP）共用一个预算：单轮迭代因此有界，不会把等待拖过调用方总预算
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        TimeSpan wait = timeout is { } budget && budget < s_webProbeTimeout ? budget : s_webProbeTimeout;
        cts.CancelAfter(wait < TimeSpan.Zero ? TimeSpan.Zero : wait);

        if (!await IsLoopbackServingAsync(port, ct: cts.Token).ConfigureAwait(false))
        {
            return LoopbackWebProbe.NotServing;
        }

        try
        {
            using HttpResponseMessage response = await s_webProbeClient
                .GetAsync(new Uri($"http://127.0.0.1:{port}/"), HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                // 无 cookie 的裸请求可能被引导到带 token 的 URL：重定向即证明路由与认证已挂载
                return LoopbackWebProbe.Ready;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return await body.ReadAsync(new byte[1], cts.Token).ConfigureAwait(false) > 0
                ? LoopbackWebProbe.Ready
                : LoopbackWebProbe.ServingNotReady;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // 连接被拒/应答超时/应答中断：web 面尚未可服务（空体 404 就是「路由未挂载」的形态）
            return LoopbackWebProbe.ServingNotReady;
        }
    }

    /// <summary>bind 探测结果三态。</summary>
    public enum LoopbackBindProbe
    {
        /// <summary>端口可 bind：dsh 此刻 bind 该端口不会立即失败。</summary>
        Free,

        /// <summary>端口被占（EADDRINUSE）：dsh bind 必败——dsh 在 bind 前要先加载整棵插件树（实测 42–47s，
        /// ADR port-wait-compression），注定失败的 spawn 是纯延迟。</summary>
        Occupied,

        /// <summary>不可判定（权限等其他 bind 失败）：按空闲处理，走既有 spawn 路径（fail open 向现状）。</summary>
        Indeterminate,
    }

    /// <summary>自有 bind 探测：以独占 bind 试占该回环端口，判定 dsh 的固定端口 bind 是否必败（ADR port-wait-compression）。
    /// 与 <see cref="IsLoopbackServingAsync"/> 的连接探测互补——bind 探测连「已 bind 未 accept」的占用者也能判出。</summary>
    /// <param name="port">端口。</param>
    /// <returns>三态判定。</returns>
    public static LoopbackBindProbe ProbeLoopbackBind(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return LoopbackBindProbe.Free;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return LoopbackBindProbe.Occupied;
        }
        catch (SocketException)
        {
            return LoopbackBindProbe.Indeterminate;
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>读取单个 pid 的血统候选快照；非血统（无 token）或不可读返回 null。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>候选快照或 null。</returns>
    private static RuntimeLineage.Candidate? TryReadCandidate(int pid)
    {
        try
        {
            string environ = File.ReadAllText($"/proc/{pid}/environ");
            string? token = EnvValue(environ, RuntimeLineage.TokenEnv);
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            string cmdLine = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim();
            return new RuntimeLineage.Candidate(
                pid,
                token,
                EnvValue(environ, RuntimeLineage.LineageHomeEnv),
                cmdLine,
                TryReadStartTime(pid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 进程已死/无权限：不是血统候选（零误杀方向）
            return null;
        }
    }

    /// <summary>读进程起始时刻（用于区分「接力续任者」与「更早的残留实例」）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>起始时刻；读不到返回 null（按不可证新生处理）。</returns>
    private static DateTimeOffset? TryReadStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>从 <c>/proc/&lt;pid&gt;/environ</c> 内容取某环境变量值（NUL 分隔的 <c>NAME=VALUE</c> 序列）。</summary>
    /// <param name="environ">environ 原文。</param>
    /// <param name="name">变量名。</param>
    /// <returns>取值；不存在返回 null。</returns>
    private static string? EnvValue(string environ, string name)
    {
        string prefix = name + "=";
        foreach (string entry in environ.Split('\0'))
        {
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                return entry[prefix.Length..];
            }
        }

        return null;
    }
}
