using System.Text;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>单实例仲裁契约：锁地址 dev 隔离、二启判定、通知应答、残留自愈、unlink 清理。</summary>
public class LauncherActivationTests
{
    [Fact]
    public void SocketPath_DevSuffix_IsolatesDevFromProd()
    {
        var dir = NewDir();
        var prod = LauncherActivation.SocketPath(dir, "app", isDev: false);
        var dev = LauncherActivation.SocketPath(dir, "app", isDev: true);

        Assert.Equal(Path.Combine(dir, "app.sock"), prod);
        Assert.Equal(Path.Combine(dir, "app.dev.sock"), dev);
        Assert.NotEqual(prod, dev);
    }

    [Fact]
    public async Task PrimaryThenNotify_SecondaryGetsAck_AndCallbackFires()
    {
        var path = NewSocketPath();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(LauncherActivation.TryBindPrimary(path, () => fired.TrySetResult(), null, out var listener));
        using (listener)
        {
            // 二启视角：地址被占 → 通知可达且收到应答
            Assert.True(LauncherActivation.NotifyPrimary(path, TimeSpan.FromSeconds(2)));
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task TryBindPrimary_SecondBindWhileAlive_ReturnsFalse()
    {
        var path = NewSocketPath();
        Assert.True(LauncherActivation.TryBindPrimary(path, () => { }, null, out var first));
        using (first)
        {
            await Task.Delay(50); // 给 accept loop 起来留一拍，模拟稳态
            Assert.False(LauncherActivation.TryBindPrimary(path, () => { }, null, out _));
        }
    }

    [Fact]
    public void NotifyPrimary_NoListener_ReturnsFalse()
    {
        var path = Path.Combine(NewDir(), "absent.sock");
        Assert.False(LauncherActivation.NotifyPrimary(path, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task StaleSocketFile_SelfHeals_AndRebinds()
    {
        var path = NewSocketPath();
        // 崩溃残留形态：文件存在但对端不存在（探活必败）
        File.WriteAllText(path, "stale");

        Assert.True(LauncherActivation.TryBindPrimary(path, () => { }, null, out var listener));
        using (listener)
        {
            await Task.Delay(50);
            Assert.True(File.Exists(path)); // 已重建为活 socket
            // 主实例视角功能完好
            Assert.True(LauncherActivation.NotifyPrimary(path, TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task Listener_Dispose_UnlinksSocket_AndAllowsRebind()
    {
        var path = NewSocketPath();
        Assert.True(LauncherActivation.TryBindPrimary(path, () => { }, null, out var listener));
        listener!.Dispose();

        Assert.False(File.Exists(path)); // unlink 清理
        Assert.True(LauncherActivation.TryBindPrimary(path, () => { }, null, out var rebound));
        using (rebound)
        {
            await Task.Delay(50);
            Assert.True(File.Exists(path));
        }
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("")]
    public async Task Serve_IgnoresForeignPayload_KeepsListening(string payload)
    {
        var path = NewSocketPath();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(LauncherActivation.TryBindPrimary(path, () => fired.TrySetResult(), null, out var listener));
        using (listener)
        {
            SendRaw(path, payload); // 垃圾输入：不应误触发回调也不应断链

            Assert.True(LauncherActivation.NotifyPrimary(path, TimeSpan.FromSeconds(2)));
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(2)); // 合法命令仍可达
        }
    }

    private static void SendRaw(string path, string payload)
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
        socket.Send(Encoding.UTF8.GetBytes(payload + "\n"));
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string NewSocketPath() => Path.Combine(NewDir(), "instance.sock");
}
