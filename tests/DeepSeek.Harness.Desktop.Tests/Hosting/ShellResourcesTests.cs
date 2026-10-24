using System.Net;
using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>进程级资源的提前端口释放（ADR restart-proxy-port-release）：<c>ReleaseLocalPorts</c> 必须覆盖
/// 回环代理监听 socket——只删掉其中的 <c>StopListener</c>，本组立即红（行为级回归，而不是只测原语）。</summary>
public class ShellResourcesTests
{
    /// <summary>释放动作放掉代理监听端口：同进程的继任者可立刻绑回同一端口，不必等 <c>Run</c> 尾部收摊。</summary>
    [Fact]
    public void ReleaseLocalPorts_FreesProxyListenerPort()
    {
        DshLoopbackProxy.ProxySetup proxy = DshLoopbackProxy.TryCreate(
            new DshShellForward(),
            Path.GetTempPath(),
            _ => { },
            loadShellPort: () => null,
            persistShellPort: _ => { });
        DshLoopbackProxy bound = Assert.IsType<DshLoopbackProxy>(proxy.Proxy);
        int port = bound.Url.Port;
        var resources = new ShellResources();
        resources.Attach(proxy);
        try
        {
            resources.ReleaseLocalPorts();

            using var successor = new TcpListener(IPAddress.Loopback, port);
            successor.Start();
            Assert.Equal(port, ((IPEndPoint)successor.LocalEndpoint).Port);
        }
        finally
        {
            resources.Dispose();
        }
    }
}
