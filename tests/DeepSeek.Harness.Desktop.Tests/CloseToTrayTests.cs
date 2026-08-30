using DeepSeek.Harness.Desktop.Services.Tray;
using Ryn.Ipc;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>「关闭时最小化到托盘」偏好：缺省 true（历史行为）、损坏回退、读写往返。</summary>
public class CloseBehaviorPreferenceTests
{
    private static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ddc-close-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, CloseBehaviorPreference.FileName);
    }

    [Fact]
    public void MissingFile_FallsBackToTrue()
    {
        // 存量升级零感知：无持久化文件时保持历史行为（托盘可用即隐藏）
        Assert.True(new CloseBehaviorPreference(TempPath()).HideOnClose);
    }

    [Fact]
    public void CorruptFile_FallsBackToTrue()
    {
        string path = TempPath();
        File.WriteAllText(path, "{not-json");
        Assert.True(new CloseBehaviorPreference(path).HideOnClose);
    }

    [Fact]
    public void SetFalse_PersistsAndSurvivesReload()
    {
        string path = TempPath();
        var pref = new CloseBehaviorPreference(path);
        pref.Set(false);
        Assert.False(pref.HideOnClose);
        Assert.False(new CloseBehaviorPreference(path).HideOnClose);
        Assert.Contains("\"hideToTrayOnClose\":false", File.ReadAllText(path));
    }

    [Fact]
    public void SetTrue_WritesExplicitTrue()
    {
        string path = TempPath();
        new CloseBehaviorPreference(path).Set(true);
        Assert.Contains("\"hideToTrayOnClose\":true", File.ReadAllText(path));
    }
}

/// <summary>closeToTray 路由契约：帧形状、set 落盘、未知命令 fail loud。</summary>
public class CloseToTrayCommandRouterTests
{
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
