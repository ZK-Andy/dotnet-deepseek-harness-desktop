using System.Net;
using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>loopback bind 探针（Infrastructure 边界探测面）平台契约：空闲判 Free、监听态占用判 Occupied、
/// 已 bind 未 listen 的内核语义固化（Linux-only 断言）。</summary>
public class RuntimeLineageProbeTests
{

    /// <summary>bind 探测：空闲端口判 Free——dsh 此刻 bind 不会立即失败（ADR port-wait-compression）。</summary>
    [Fact]
    public void ProbeLoopbackBind_FreePort_ReturnsFree()
    {
        // 由 OS 分配一个当前空闲的端口：bind 一次拿端口号后释放，再交给被测探测。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Equal(RuntimeLineageProbes.LoopbackBindProbe.Free, RuntimeLineageProbes.ProbeLoopbackBind(port));
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
}
