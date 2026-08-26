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
        Assert.True(LauncherActivation.TryBindPrimary(
            path,
            () =>
            {
                fired.TrySetResult();
                return Task.CompletedTask;
            },
            null,
            out var listener));
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
        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out var first));
        using (first)
        {
            await Task.Delay(50); // 给 accept loop 起来留一拍，模拟稳态
            Assert.False(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out _));
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

        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out var listener));
        using (listener)
        {
            await Task.Delay(50);
            Assert.True(File.Exists(path)); // 已重建为活 socket
            // 主实例视角功能完好
            Assert.True(LauncherActivation.NotifyPrimary(path, TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public void Listener_Dispose_UnlinksSocket_AndAllowsRebind()
    {
        var path = NewSocketPath();
        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out var listener));
        listener!.Dispose();

        Assert.False(File.Exists(path)); // unlink 清理
        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out var rebound));
        using (rebound)
        {
            Assert.True(File.Exists(path)); // 已重建为活 socket
        }
    }

    [Fact]
    public void Listener_Dispose_Twice_IsIdempotent_NoThrow()
    {
        // 双击托盘退出会二次进入退出编排：Dispose 必须幂等，
        // 不得以 ObjectDisposedException 在路由层炸出误导性「退出关窗失败」
        var path = NewSocketPath();
        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out var listener));
        listener!.Dispose();
        listener.Dispose(); // 第二次必须安全

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void StaleUndeletableSocket_DegradesToPrimaryWithoutListener_NotZeroInstance()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // UDS 仲裁仅 Unix 启用
        }

        var dir = NewDir();
        var path = Path.Combine(dir, "instance.sock");
        File.WriteAllText(path, "stale");
        var dirInfo = new DirectoryInfo(dir);

        // root 会无视目录写位：先探针判定权限模型是否生效，未生效则本用例无断言意义
        dirInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var permsEnforced = !TryDelete(path);
        dirInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 探针阶段残留：目录已恢复可写，删除失败不影响后续布置（唯一临时目录）
        }

        if (!permsEnforced)
        {
            return;
        }

        File.WriteAllText(path, "stale");
        dirInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        try
        {
            // 残留清不掉 → 降级为主实例无监听照常启动（true + null listener）；
            // 绝不返回 false 让调用方误判「有存活主实例」而陪葬成零实例
            Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out var listener));
            Assert.Null(listener);
        }
        finally
        {
            dirInfo.UnixFileMode |= UnixFileMode.UserWrite; // 恢复可删，便于临时目录清理
        }
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("")]
    public async Task Serve_IgnoresForeignPayload_KeepsListening(string payload)
    {
        var path = NewSocketPath();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(LauncherActivation.TryBindPrimary(
            path,
            () =>
            {
                fired.TrySetResult();
                return Task.CompletedTask;
            },
            null,
            out var listener));
        using (listener)
        {
            SendRaw(path, payload); // 垃圾输入：不应误触发回调也不应断链

            Assert.True(LauncherActivation.NotifyPrimary(path, TimeSpan.FromSeconds(2)));
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(2)); // 合法命令仍可达
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            // 权限生效时目录内条目不可删：探针据此判定权限模型是否被当前用户绕过（root）
            return false;
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
