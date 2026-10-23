using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Tests.Fakes;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>G3 首批命令面（ADR avalonia-g3-shell-commands）：三条路由的覆盖面与两个装配期单例
/// （关窗偏好端口、诊断导出端口）的接线契约。</summary>
public class ShellCommandRegistrationTests
{
    private static string NewPreferencePath() =>
        Path.Combine(Path.GetTempPath(), "dsh-shell-commands", Guid.NewGuid().ToString("N"),
            "desktop-preferences.json");

    private static IReadOnlyList<ICommandRouter> CreateRouters(Func<bool> trayReady, string preferencePath) =>
        ShellCommandRegistration.CreateRouters(
            new CloseBehaviorPreference(preferencePath),
            trayReady,
            new FakeDiagnosticsExport(),
            log: _ => { });

    /// <summary>验证路由表覆盖自启/关窗到托盘/诊断导出五条命令，且不侵占页面桥与窗口命令。</summary>
    [Fact]
    public void Routers_CoverTheThreeCommandFamilies()
    {
        IReadOnlyList<ICommandRouter> routers = CreateRouters(() => true, NewPreferencePath());
        string[] owned =
        [
            HostCommandIds.AutostartGetState,
            HostCommandIds.AutostartSet,
            HostCommandIds.CloseToTrayGetState,
            HostCommandIds.CloseToTraySet,
            HostCommandIds.DiagnosticsExport,
        ];
        string[] foreign =
        [
            HostCommandIds.AppRestart,
            HostCommandIds.UpdateGetState,
            HostCommandIds.TrayEvent,
            HostCommandIds.BootstrapRetry,
            HostCommandIds.RecoveryExit,
            HostCommandIds.WindowClose,
        ];

        foreach (string command in owned)
        {
            Assert.Contains(routers, router => router.CanRoute(command));
        }

        foreach (string command in foreign)
        {
            Assert.DoesNotContain(routers, router => router.CanRoute(command));
        }
    }

    /// <summary>验证 closeToTray 状态帧携带装配期托盘就绪判定，且 set 落盘后读回同一实例。</summary>
    [Fact]
    public async Task CloseToTray_CarriesTrayReadiness_AndPersistsPreference()
    {
        string path = NewPreferencePath();
        var preference = new CloseBehaviorPreference(path);
        IReadOnlyList<ICommandRouter> routers = ShellCommandRegistration.CreateRouters(
            preference, () => false, new FakeDiagnosticsExport(), log: _ => { });
        ICommandRouter? router = routers.FirstOrDefault(r => r.CanRoute(HostCommandIds.CloseToTrayGetState));
        Assert.NotNull(router);

        string initial = await router.RouteAsync(
            HostCommandIds.CloseToTrayGetState, ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None);
        Assert.Equal("""{"enabled":true,"available":false}""", initial);

        await router.RouteAsync(
            HostCommandIds.CloseToTraySet,
            System.Text.Encoding.UTF8.GetBytes("""{"enabled":false}"""),
            null!,
            CancellationToken.None);

        Assert.False(preference.HideOnClose);
        Assert.False(new CloseBehaviorPreference(path).HideOnClose);
    }

    /// <summary>验证诊断导出命令走注入端口并回传落盘路径（不落真实 zip）。</summary>
    [Fact]
    public async Task DiagnosticsExport_RoutesThroughThePort()
    {
        IReadOnlyList<ICommandRouter> routers = CreateRouters(() => true, NewPreferencePath());
        ICommandRouter router = Assert.Single(
            routers, r => r.CanRoute(HostCommandIds.DiagnosticsExport));

        string frame = await router.RouteAsync(
            HostCommandIds.DiagnosticsExport, ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None);

        Assert.Contains("/tmp/fake-diagnostics.zip", frame, StringComparison.Ordinal);
    }
}
