using System.Net;
using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>loopback bind 探针（Infrastructure 边界探测面）平台契约：空闲判 Free、监听态占用判 Occupied、
/// 已 bind 未 listen 的内核语义固化（Linux-only 断言）；另钉血统 token 的 environ 读取（新名优先 + 旧名回退）。</summary>
public class RuntimeLineageProbeTests
{
    /// <summary>血统 token 读取：新名优先；取不到（含空值）回退旧名；两者都无则 null。
    /// 旧名回退是升级窗口的必需项——上一版 `.dsh-pid` 记录只带旧名，只读新名会让孤儿复验失配而被跳过。</summary>
    [Fact]
    public void ReadTokenFromEnviron_PrefersNewName_FallsBackToLegacy()
    {
        string current = RuntimeLineage.TokenEnv;
        string legacy = RuntimeLineage.LegacyTokenEnv;

        Assert.Equal("new-tok", RuntimeLineageProbes.ReadTokenFromEnviron($"{current}=new-tok\0PATH=/usr/bin"));
        Assert.Equal("old-tok", RuntimeLineageProbes.ReadTokenFromEnviron($"{legacy}=old-tok\0PATH=/usr/bin"));
        Assert.Equal("new-tok", RuntimeLineageProbes.ReadTokenFromEnviron($"{legacy}=old-tok\0{current}=new-tok"));
        Assert.Equal("old-tok", RuntimeLineageProbes.ReadTokenFromEnviron($"{current}=\0{legacy}=old-tok"));
        Assert.Null(RuntimeLineageProbes.ReadTokenFromEnviron("PATH=/usr/bin\0HOME=/home/u"));
        Assert.Null(RuntimeLineageProbes.ReadTokenFromEnviron(string.Empty));
    }

    /// <summary>bind 探测：空闲端口判 Free——dsh 此刻 bind 不会立即失败（ADR port-wait-compression）。</summary>
    [Fact]
    public void ProbeLoopbackBind_FreePort_ReturnsFree()
    {
        // 由 OS 分配一个当前空闲的端口（共享夹具同法），再交给被测探测。
        Assert.Equal(RuntimeLineageProbes.LoopbackBindProbe.Free, RuntimeLineageProbes.ProbeLoopbackBind(LoopbackHttpResponder.ReserveFreePort()));
    }

    /// <summary>bind 探测：被监听者占住的端口判 Occupied——正是要拦掉的「注定失败尝试」形态。</summary>
    [Fact]
    public void ProbeLoopbackBind_OccupiedPort_ReturnsOccupied()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.Equal(RuntimeLineageProbes.LoopbackBindProbe.Occupied, RuntimeLineageProbes.ProbeLoopbackBind(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Linux 内核只对 LISTEN 态 socket 判 EADDRINUSE：已 bind 未 listen 的占用者探测为 Free——
    /// 固化该平台行为防误判（真实占用者 dsh/占位进程都是监听态，主判据不受影响；非 Linux 不断言）。</summary>
    [Fact]
    public void ProbeLoopbackBind_BoundButNotListening_Free_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 平台行为差异不纳入断言
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.LocalEndPoint!).Port;

        Assert.Equal(RuntimeLineageProbes.LoopbackBindProbe.Free, RuntimeLineageProbes.ProbeLoopbackBind(port));
    }

    /// <summary>web 就绪探针：无人监听判 NotServing——续任者尚未 bind，谈不上就绪（ADR relay-web-readiness）。</summary>
    [Fact]
    public async Task ProbeLoopbackWebAsync_NoListener_ReturnsNotServing()
    {
        int port = LoopbackHttpResponder.ReserveFreePort();

        Assert.Equal(
            RuntimeLineageProbes.LoopbackWebProbe.NotServing,
            await RuntimeLineageProbes.ProbeLoopbackWebAsync(port, CancellationToken.None));
    }

    /// <summary>web 就绪探针：端口可连但 HTTP 无声判 ServingNotReady——正是「dsh 已 bind、web-runtime 行
    /// 尚未挂载」的窗口，也是导航进空白页的那段。</summary>
    [Fact]
    public async Task ProbeLoopbackWebAsync_PortListenedWithoutHttpAnswer_ReturnsServingNotReady()
    {
        var listener = new TcpListener(IPAddress.Loopback, LoopbackHttpResponder.ReserveFreePort());
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            // 只监不听不应答：TCP 探活连得上，HTTP 请求挂到探测超时
            Assert.Equal(
                RuntimeLineageProbes.LoopbackWebProbe.ServingNotReady,
                await RuntimeLineageProbes.ProbeLoopbackWebAsync(
                    port, CancellationToken.None, TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>web 就绪探针：带响应体的 HTTP 应答判 Ready——dsh 无 cookie 时的 401 文案体即就绪形态。</summary>
    [Fact]
    public async Task ProbeLoopbackWebAsync_HttpBodyAnswer_ReturnsReady()
    {
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var cts = new CancellationTokenSource();
        (TcpListener listener, Task serving) = LoopbackHttpResponder.Start(
            port,
            LoopbackHttpResponder.Response("HTTP/1.1 401 Unauthorized", "dsh web authentication required; probe"),
            cts.Token);
        try
        {
            Assert.Equal(
                RuntimeLineageProbes.LoopbackWebProbe.Ready,
                await RuntimeLineageProbes.ProbeLoopbackWebAsync(port, CancellationToken.None));
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await serving;
        }
    }

    /// <summary>web 就绪探针：空体应答判 ServingNotReady——路由未挂载的空体 404 不得被当成就绪。</summary>
    [Fact]
    public async Task ProbeLoopbackWebAsync_EmptyBodyAnswer_ReturnsServingNotReady()
    {
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var cts = new CancellationTokenSource();
        (TcpListener listener, Task serving) = LoopbackHttpResponder.Start(
            port, LoopbackHttpResponder.Response("HTTP/1.1 404 Not Found"), cts.Token);
        try
        {
            Assert.Equal(
                RuntimeLineageProbes.LoopbackWebProbe.ServingNotReady,
                await RuntimeLineageProbes.ProbeLoopbackWebAsync(port, CancellationToken.None));
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await serving;
        }
    }

    /// <summary>web 就绪探针：3xx 重定向判 Ready——无 cookie 的裸请求被引导到带 token 的 URL 即证明路由已挂载。</summary>
    [Fact]
    public async Task ProbeLoopbackWebAsync_RedirectAnswer_ReturnsReady()
    {
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var cts = new CancellationTokenSource();
        (TcpListener listener, Task serving) = LoopbackHttpResponder.Start(
            port,
            LoopbackHttpResponder.Response("HTTP/1.1 302 Found", headers: "Location: /?token=probe\r\n"),
            cts.Token);
        try
        {
            Assert.Equal(
                RuntimeLineageProbes.LoopbackWebProbe.Ready,
                await RuntimeLineageProbes.ProbeLoopbackWebAsync(port, CancellationToken.None));
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await serving;
        }
    }
}
