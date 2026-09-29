using System.Net;
using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>回环转发代理（ADR loopback-forward-proxy）：loopback 纯监听的最小 HTTP/1.1 转发器，
/// 把页面流量按铸币路由送往 dsh。SSE/未知长度流式直通（Ryn scheme 通道必物化，流只能走 http 源）；
/// Ryn IPC（`/ipc/*`）不经过本代理（Ryn dev-server 分支给页面注绝对 `_ipcBase`，直连 Ryn 服）。
/// 信任边界 = loopback 绑定本身（非回环不可达）；token/cookie 不出本机。
/// 中继面（受理/路由/回写）在 <c>DshLoopbackProxy.Relay.cs</c>（partial，ADR 尺寸健康闸 F1）。</summary>
public sealed partial class DshLoopbackProxy : IDisposable
{
    // 请求头上限：Ryn 本地 IPC 服务同口径（32KB），超限 loud 502。
    private readonly DshShellForward _forward;
    private readonly Action<string> _log;
    private readonly HttpClient _client;
    private readonly TcpListener _listener;
    private readonly DshLoopbackLocal _local;
    private readonly DshLoopbackTunnel _tunnel;
    private bool _disposed;

    /// <summary>代理源（窗口 URL 与探针/守卫口径家；端口记忆优先、冲突 OS 分配，构造即绑定）。</summary>
    public Uri Url { get; }

    /// <summary>代理 origin（authority 形；Ryn dev-server 分支的 CORS 信任单位）。</summary>
    public string Origin => Url.GetLeftPart(UriPartial.Authority);

    /// <summary>构造并绑定回环代理（读代理端口记忆 → 试绑 → 冲突降级 OS 分配；dsh 通道另配，见重载）。</summary>
    /// <param name="forward">壳转发器（铸币态家）。</param>
    /// <param name="log">日志回调（入口/终态 loud；值永不落盘）。</param>
    /// <param name="contentRoot">引导页静态根（wwwroot；null 即无指南面，指南请求 502）。</param>
    public DshLoopbackProxy(DshShellForward forward, Action<string> log, string? contentRoot)
        : this(forward, log, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, contentRoot,
            HarnessRuntimeHost.TryLoadShellPort(), HarnessRuntimeHost.PersistShellPort)
    {
    }

    /// <summary>启动回环代理源并返回结果对象（组合根值流：消费段收参，不再借组合根字段回填——
    /// ADR compose-root-form-separation）。绑定失败 loud 后降级（Proxy 为 null，窗口走 wwwroot，
    /// 行为与 dsh 未起一致，不挡启动）；绑定异常类型清单（协议/平台策略）住本类，不散在组合根。</summary>
    /// <param name="forward">壳转发器（铸币态家）。</param>
    /// <param name="contentRoot">引导页静态根（wwwroot；未铸币时本地 holder/指南面）。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="loadShellPort">代理端口记忆读取缝（缺省读 profile state <c>.dsh-shell-port</c>；测试注入密闭）。
    /// 契约：不得抛出——在绑定 try 段内执行，抛出被列异常会被误判为「绑定失败」降级（已绑定 listener 泄漏）；
    /// 生产静态对吞 IO 异常恒不抛。</param>
    /// <param name="persistShellPort">代理端口持久化缝（缺省写 profile state；测试注入密闭）。契约：不得抛出（同上）。</param>
    public static ProxySetup TryCreate(DshShellForward forward, string contentRoot, Action<string> log,
        Func<int?>? loadShellPort = null, Action<int>? persistShellPort = null)
    {
        // 构造（绑定）先行：绑定异常即 fail loud/降级，无资源泄漏面；CTS 只在绑定成功后创建，
        // 其寿命随 ProxySetup 交组合根尾部统一释放（cancel → dispose → dispose proxy）。
        DshLoopbackProxy proxy;
        try
        {
            proxy = new DshLoopbackProxy(forward, log, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false },
                contentRoot,
                (loadShellPort ?? HarnessRuntimeHost.TryLoadShellPort)(),
                persistShellPort ?? HarnessRuntimeHost.PersistShellPort);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or ArgumentException)
        {
            log($"[shell] 代理源绑定失败（降级 wwwroot）：{ex.GetType().Name} {ex.Message}");
            return new ProxySetup(null, new CancellationTokenSource());
        }

        CancellationTokenSource cts = new();
        _ = proxy.RunAsync(cts.Token);
        return new ProxySetup(proxy, cts);
    }

    /// <summary>代理源启动产出（组合根值流管线阶段产出）：Cts 恒非空（随组合根 Run 尾部统一释放），
    /// Proxy 仅绑定成功非空。</summary>
    /// <param name="Proxy">回环代理源；null = 绑定失败已降级 wwwroot。</param>
    /// <param name="Cts">代理受理循环取消令牌源。</param>
    public readonly record struct ProxySetup(DshLoopbackProxy? Proxy, CancellationTokenSource Cts)
    {
        /// <summary>组合根 Run 尾部释放（先停受理循环再放代理；顺序与搬迁前 finally 一致）。</summary>
        public void Dispose()
        {
            Cts.Cancel();
            Cts.Dispose();
            Proxy?.Dispose();
        }
    }

    /// <summary>测试缝：注入 dsh 通道传输；可选注入代理端口记忆（缺省 OS 分配、不持久化）。</summary>
    internal DshLoopbackProxy(DshShellForward forward, Action<string> log, HttpMessageHandler transport,
        string? contentRoot = null, int? preferredPort = null, Action<int>? persistPort = null)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(transport);
        _forward = forward;
        _log = log;
        // 流式直通：Timeout 无限（SSE 空闲不断），寿命与页 socket 绑定（页断联即 cancel，
        // EventSource 自重连；见 ADR loopback-forward-proxy）。
        _client = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        _local = new DshLoopbackLocal(forward, log, contentRoot);
        _tunnel = new DshLoopbackTunnel(forward, log);
        _listener = BindListener(preferredPort, persistPort);
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = new Uri($"http://localhost:{port}/", UriKind.Absolute);
        log($"[shell] 代理源就绪：{Origin}（回环独占；dsh 内容经此源，Ryn IPC 走 Ryn 服）");
    }

    /// <summary>代理端口绑定序列（ADR shell-proxy-port-persistence）：有记忆试绑记忆端口（成功即用，
    /// 值不变不写盘）；被占 loud + OS 重分配；无记忆/损坏 OS 分配——两种漂移形态的新端口都立即持久化
    /// （下次起点即新端口）。</summary>
    private TcpListener BindListener(int? preferredPort, Action<int>? persistPort)
    {
        if (preferredPort is int port)
        {
            TcpListener preferred = CreateListener(port);
            try
            {
                preferred.Start();
                return preferred;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                preferred.Stop();
                _log($"[shell] 代理记忆端口 {port} 已被占：降级 OS 分配并重新记忆（本次重启 origin 漂移，上次会话恢复失效一次）");
            }
        }

        TcpListener listener = CreateListener(0);
        listener.Start();
        persistPort?.Invoke(((IPEndPoint)listener.LocalEndpoint).Port);
        return listener;
    }

    /// <summary>构造回环 listener（Start 前置）。非 Windows 预设 <c>SO_REUSEADDR</c>：有序退出时壳侧主动
    /// 关闭的页连接（SSE/keep-alive）留 TIME_WAIT，快速重启试绑记忆端口否则假性 EADDRINUSE 无谓漂移；
    /// Linux/macOS 下 REUSEADDR 不放开对存活 LISTEN 的冲突，被占降级语义不变。Windows 的 REUSEADDR
    /// 允许绑定到存活监听（劫持语义）故不设——其 TIME_WAIT 边缘为已知局限（见 ADR）。</summary>
    private static TcpListener CreateListener(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        if (!OperatingSystem.IsWindows())
        {
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        return listener;
    }

    /// <summary>受理循环直至取消；单连接失败只记 loud，不断服。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleConnectionAsync(client, ct);
        }
    }

    /// <summary>停止监听并释放 dsh 通道（在途连接随 socket 关闭收尾）。</summary>
    public void Dispose()
    {
        _disposed = true;
        _listener.Stop();
        _client.Dispose();
    }
}
