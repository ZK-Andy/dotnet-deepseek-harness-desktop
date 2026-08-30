using System.Text;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>单实例仲裁契约：锁地址 dev 隔离、二启判定、通知应答、残留自愈、unlink 清理。</summary>
public class LauncherActivationTests
{
    /// <summary>验证 isDev 开关使 socket 路径带 .dev 后缀，开发与生产实例不争抢同一把锁。</summary>
    [Fact]
    public void SocketPath_DevSuffix_IsolatesDevFromProd()
    {
        string dir = NewDir();
        string prod = LauncherActivation.SocketPath(dir, "app", isDev: false);
        string dev = LauncherActivation.SocketPath(dir, "app", isDev: true);

        Assert.Equal(Path.Combine(dir, "app.sock"), prod);
        Assert.Equal(Path.Combine(dir, "app.dev.sock"), dev);
        Assert.NotEqual(prod, dev);
    }

    /// <summary>验证回退目录名带稳定非空的 uid 数字后缀做跨用户隔离，同用户两次调用结果一致；Windows 平台（不启用 UDS 仲裁）恒为空串。</summary>
    [Fact]
    public void FallbackUidSuffix_IsPerUser_NonEmpty()
    {
        // 临时目录回退的跨用户隔离：Linux/macOS 带 uid 数字后缀，同用户两次调用稳定；
        // Windows 不启用 socket 仲裁（平台边界），恒空串
        string suffix = LauncherActivation.FallbackUidSuffix();
        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(suffix);
            return;
        }

        Assert.NotEmpty(suffix);
        Assert.Equal(suffix, LauncherActivation.FallbackUidSuffix());
    }

    /// <summary>验证主实例绑定成功后，二启进程 NotifyPrimary 能得到应答，且主实例注册的激活回调被触发。</summary>
    [Fact]
    public async Task PrimaryThenNotify_SecondaryGetsAck_AndCallbackFires()
    {
        string path = NewSocketPath();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(LauncherActivation.TryBindPrimary(
            path,
            () =>
            {
                fired.TrySetResult();
                return Task.CompletedTask;
            },
            null,
            out PrimaryListener? listener));
        using (listener)
        {
            // 二启视角：地址被占 → 通知可达且收到应答
            Assert.True(LauncherActivation.NotifyPrimary(path, TimeSpan.FromSeconds(2)));
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>验证主实例存活期间再次 TryBindPrimary 返回 false，二启被正确判定而非重复创建监听。</summary>
    [Fact]
    public async Task TryBindPrimary_SecondBindWhileAlive_ReturnsFalse()
    {
        string path = NewSocketPath();
        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out PrimaryListener? first));
        using (first)
        {
            await Task.Delay(50); // 给 accept loop 起来留一拍，模拟稳态
            Assert.False(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out _));
        }
    }

    /// <summary>验证目标 socket 文件不存在（无监听对端）时通知失败返回 false。</summary>
    [Fact]
    public void NotifyPrimary_NoListener_ReturnsFalse()
    {
        string path = Path.Combine(NewDir(), "absent.sock");
        Assert.False(LauncherActivation.NotifyPrimary(path, TimeSpan.FromMilliseconds(500)));
    }

    /// <summary>验证文件残留但对端不存在的崩溃态能被探活识别并自愈重建为活 socket，通知功能随后恢复可用。</summary>
    [Fact]
    public async Task StaleSocketFile_SelfHeals_AndRebinds()
    {
        string path = NewSocketPath();
        // 崩溃残留形态：文件存在但对端不存在（探活必败）
        File.WriteAllText(path, "stale");

        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out PrimaryListener? listener));
        using (listener)
        {
            await Task.Delay(50);
            Assert.True(File.Exists(path)); // 已重建为活 socket
            // 主实例视角功能完好
            Assert.True(LauncherActivation.NotifyPrimary(path, TimeSpan.FromSeconds(2)));
        }
    }

    /// <summary>验证 Dispose 时 unlink 掉 socket 文件，且释放后同一地址可立即重新绑定。</summary>
    [Fact]
    public void Listener_Dispose_UnlinksSocket_AndAllowsRebind()
    {
        string path = NewSocketPath();
        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out PrimaryListener? listener));
        listener!.Dispose();

        Assert.False(File.Exists(path)); // unlink 清理
        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out PrimaryListener? rebound));
        using (rebound)
        {
            Assert.True(File.Exists(path)); // 已重建为活 socket
        }
    }

    /// <summary>验证 Dispose 二次调用幂等、不抛 ObjectDisposedException，覆盖双击托盘退出触发重复清理的路径。</summary>
    [Fact]
    public void Listener_Dispose_Twice_IsIdempotent_NoThrow()
    {
        // 双击托盘退出会二次进入退出编排：Dispose 必须幂等，
        // 不得以 ObjectDisposedException 在路由层炸出误导性「退出关窗失败」
        string path = NewSocketPath();
        Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out PrimaryListener? listener));
        listener!.Dispose();
        listener.Dispose(); // 第二次必须安全

        Assert.False(File.Exists(path));
    }

    /// <summary>验证残留 socket 因只读目录删不掉时降级为「主实例但无监听」（返回 true + null listener），绝不返回 false 让调用方误判存在存活主实例而陪葬成零实例。</summary>
    [Fact]
    public void StaleUndeletableSocket_DegradesToPrimaryWithoutListener_NotZeroInstance()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // UDS 仲裁仅 Unix 启用
        }

        string dir = NewDir();
        string path = Path.Combine(dir, "instance.sock");
        File.WriteAllText(path, "stale");
        var dirInfo = new DirectoryInfo(dir);

        // root 会无视目录写位：先探针判定权限模型是否生效，未生效则本用例无断言意义
        dirInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        bool permsEnforced = !TryDelete(path);
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
            Assert.True(LauncherActivation.TryBindPrimary(path, () => Task.CompletedTask, null, out PrimaryListener? listener));
            Assert.Null(listener);
        }
        finally
        {
            dirInfo.UnixFileMode |= UnixFileMode.UserWrite; // 恢复可删，便于临时目录清理
        }
    }

    /// <summary>验证垃圾/空载荷不会误触发激活回调也不断链，随后发送的合法命令仍能到达并触发回调。</summary>
    [Theory]
    [InlineData("junk")]
    [InlineData("")]
    public async Task Serve_IgnoresForeignPayload_KeepsListening(string payload)
    {
        string path = NewSocketPath();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(LauncherActivation.TryBindPrimary(
            path,
            () =>
            {
                fired.TrySetResult();
                return Task.CompletedTask;
            },
            null,
            out PrimaryListener? listener));
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
        string dir = Path.Combine(Path.GetTempPath(), "dsh-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string NewSocketPath() => Path.Combine(NewDir(), "instance.sock");
}
