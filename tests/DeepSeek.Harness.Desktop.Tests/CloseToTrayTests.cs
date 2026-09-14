using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>closeToTray 路由契约：帧形状、set 落盘、未知命令 fail loud。</summary>
public class CloseToTrayCommandRouterTests
{
    /// <summary>验证路由器只接受 closeToTray 前缀命令，autostart 等非本路由命令一概拒绝。</summary>
    [Fact]
    public void CanRoute_MatchesOnlyOwnCommands()
    {
        var router = new CloseToTrayCommandRouter(
            new CloseBehaviorPreference(Path.Combine(Path.GetTempPath(), "ddc-unused.json")),
            () => true);
        Assert.True(router.CanRoute("desktop.closeToTray.getState"));
        Assert.True(router.CanRoute("desktop.closeToTray.set"));
        Assert.False(router.CanRoute("desktop.autostart.getState"));
    }

    /// <summary>验证 getState 帧携带缺省 enabled=true 与托盘可用性 available=false 两字段。</summary>
    [Fact]
    public async Task GetState_ReturnsEnabledAndAvailability()
    {
        string path = Path.Combine(Path.GetTempPath(), "ddc-close-tests", Guid.NewGuid().ToString("N"),
            CloseBehaviorPreference.FileName);
        var router = new CloseToTrayCommandRouter(new CloseBehaviorPreference(path), () => false);
        string frame = await router.RouteAsync("desktop.closeToTray.getState", ReadOnlyMemory<byte>.Empty,
            null!, CancellationToken.None);
        // 缺省 true；available=false 时客户端禁用开关
        Assert.Equal("""{"enabled":true,"available":false}""", frame);
    }

    /// <summary>验证 set 命令把 enabled=false 落盘到偏好文件，随后 getState 帧反映关闭状态与 available=true。</summary>
    [Fact]
    public async Task SetFalse_PersistsPreference()
    {
        string path = Path.Combine(Path.GetTempPath(), "ddc-close-tests", Guid.NewGuid().ToString("N"),
            CloseBehaviorPreference.FileName);
        var router = new CloseToTrayCommandRouter(new CloseBehaviorPreference(path), () => true);
        await router.RouteAsync("desktop.closeToTray.set",
            System.Text.Encoding.UTF8.GetBytes("{\"enabled\":false}"), null!, CancellationToken.None);
        Assert.False(new CloseBehaviorPreference(path).HideOnClose);

        string frame = await router.RouteAsync("desktop.closeToTray.getState", ReadOnlyMemory<byte>.Empty,
            null!, CancellationToken.None);
        Assert.Equal("""{"enabled":false,"available":true}""", frame);
    }

    /// <summary>验证未知命令路由抛 RynCommandNotFoundException，未知命令绝不静默吞掉。</summary>
    [Fact]
    public async Task UnknownCommand_ThrowsNotFound()
    {
        var router = new CloseToTrayCommandRouter(
            new CloseBehaviorPreference(Path.Combine(Path.GetTempPath(), "ddc-unused.json")), () => true);
        await Assert.ThrowsAsync<RynCommandNotFoundException>(() =>
            router.RouteAsync("desktop.closeToTray.reset", ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None)
                .AsTask());
    }
}
