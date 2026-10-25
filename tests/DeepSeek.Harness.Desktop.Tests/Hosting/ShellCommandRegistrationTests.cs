using System.Collections.Concurrent;
using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>G3 命令面（ADR avalonia-g3-shell-commands、-g3-exit-restart）：四条路由的覆盖面、
/// 两个装配期单例（关窗偏好端口、诊断导出端口）与托盘退出/重启动作的接线契约。</summary>
public class ShellCommandRegistrationTests
{
    private static string NewPreferencePath() =>
        Path.Combine(Path.GetTempPath(), "dsh-shell-commands", Guid.NewGuid().ToString("N"),
            "desktop-preferences.json");

    /// <summary>记序管道：退出动作在线程池执行，步骤记录用并发队列。</summary>
    private static ExitPipeline NewPipeline(ConcurrentQueue<string> steps) =>
        NewPipeline(steps.Enqueue);

    private static ExitPipeline NewPipeline(Action<string> record) =>
        new(
            cancelSupervisor: () => record("cancel"),
            stopHost: () => record("stop"),
            releaseMarker: () => record("release"),
            releaseLocalPorts: () => record("ports"),
            startWatchdog: () => record("watchdog"),
            hardExit: () => record("hardExit"),
            log: _ => { });

    private static IReadOnlyList<ICommandRouter> CreateRouters(Func<bool> trayReady, string preferencePath) =>
        ShellCommandRegistration.CreateRouters(
            new CloseBehaviorPreference(preferencePath),
            trayReady,
            new FakeDiagnosticsExport(),
            new CloseGate(),
            static () => { },
            log: _ => { });

    /// <summary>验证路由表覆盖自启/关窗到托盘/诊断导出/应用重启六条命令，且不侵占页面桥、窗口命令与首启引导。</summary>
    [Fact]
    public void Routers_CoverTheFourCommandFamilies()
    {
        IReadOnlyList<ICommandRouter> routers = CreateRouters(() => true, NewPreferencePath());
        string[] owned =
        [
            HostCommandIds.AutostartGetState,
            HostCommandIds.AutostartSet,
            HostCommandIds.CloseToTrayGetState,
            HostCommandIds.CloseToTraySet,
            HostCommandIds.DiagnosticsExport,
            HostCommandIds.AppRestart,
        ];
        string[] foreign =
        [
            HostCommandIds.UpdateGetState,
            HostCommandIds.CompanionReport,
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

    /// <summary>验证重启命令走退出管道：受理帧立刻回，200ms 冲刷延迟后批准闸门并按回收序拉起新实例。</summary>
    [Fact]
    public async Task AppRestart_ApprovesGate_ThenRestartsThroughPipeline()
    {
        var steps = new ConcurrentQueue<string>();
        var gate = new CloseGate();
        (_, Action restart) = ShellCommandRegistration.CreateTrayActions(
            gate, NewPipeline(steps), spawnSuccessor: () => steps.Enqueue("spawn"), log: _ => { });
        IReadOnlyList<ICommandRouter> routers = ShellCommandRegistration.CreateRouters(
            new CloseBehaviorPreference(NewPreferencePath()),
            () => true,
            new FakeDiagnosticsExport(),
            gate,
            restart,
            log: _ => { });
        ICommandRouter router = Assert.Single(routers, r => r.CanRoute(HostCommandIds.AppRestart));

        string frame = await router.RouteAsync(
            HostCommandIds.AppRestart, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Equal("{}", frame);

        string[] seen = await WaitForStepsAsync(steps, 7, TimeSpan.FromSeconds(3));
        Assert.True(gate.ExitApproved);
        Assert.Equal(["cancel", "stop", "release", "ports", "spawn", "watchdog", "hardExit"], seen);
    }

    /// <summary>验证托盘退出动作：闸门先批准（首个管道步骤即可见），再按回收序硬退。</summary>
    [Fact]
    public async Task TrayQuit_ApprovesGateBeforePipeline()
    {
        var quitGate = new CloseGate();
        var steps = new ConcurrentQueue<string>();
        (Action quit, _) = ShellCommandRegistration.CreateTrayActions(
            quitGate,
            NewPipeline(step => steps.Enqueue($"{step}:{quitGate.ExitApproved}")),
            log: _ => { });

        quit();
        Assert.True(quitGate.ExitApproved);

        string[] seen = await WaitForStepsAsync(steps, 6, TimeSpan.FromSeconds(3));
        Assert.Equal(
            ["cancel:True", "stop:True", "release:True", "ports:True", "watchdog:True", "hardExit:True"],
            seen);
    }

    /// <summary>验证 spawn 抛错（进程路径缺失/自更新换 exe 冲突）不会留下僵尸壳：管道继续硬退并武装看门狗。</summary>
    [Fact]
    public async Task TrayRestart_SpawnFailure_StillClosesWindow()
    {
        var gate = new CloseGate();
        var steps = new ConcurrentQueue<string>();
        var logs = new ConcurrentQueue<string>();
        (_, Action restart) = ShellCommandRegistration.CreateTrayActions(
            gate,
            NewPipeline(steps),
            spawnSuccessor: () =>
            {
                steps.Enqueue("spawn");
                throw new InvalidOperationException("spawn 失败");
            },
            log: logs.Enqueue);

        restart();

        string[] seen = await WaitForStepsAsync(steps, 7, TimeSpan.FromSeconds(3));
        Assert.True(gate.ExitApproved);
        Assert.Equal(["cancel", "stop", "release", "ports", "spawn", "watchdog", "hardExit"], seen);
        Assert.Contains(logs, line => line.Contains("spawn 失败", StringComparison.Ordinal));
    }

    private static async Task<string[]> WaitForStepsAsync(ConcurrentQueue<string> steps, int expected, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (steps.Count < expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(steps.Count >= expected, $"只观察到 {steps.Count}/{expected} 步：{string.Join(",", steps)}");
        return steps.ToArray();
    }

    /// <summary>验证 closeToTray 状态帧携带装配期托盘就绪判定，且 set 落盘后读回同一实例。</summary>
    [Fact]
    public async Task CloseToTray_CarriesTrayReadiness_AndPersistsPreference()
    {
        string path = NewPreferencePath();
        var preference = new CloseBehaviorPreference(path);
        IReadOnlyList<ICommandRouter> routers = ShellCommandRegistration.CreateRouters(
            preference, () => false, new FakeDiagnosticsExport(), new CloseGate(), static () => { }, log: _ => { });
        ICommandRouter? router = routers.FirstOrDefault(r => r.CanRoute(HostCommandIds.CloseToTrayGetState));
        Assert.NotNull(router);

        string initial = await router.RouteAsync(
            HostCommandIds.CloseToTrayGetState, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Equal("""{"enabled":true,"available":false}""", initial);

        await router.RouteAsync(
            HostCommandIds.CloseToTraySet,
            System.Text.Encoding.UTF8.GetBytes("""{"enabled":false}"""),
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
            HostCommandIds.DiagnosticsExport, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Contains("/tmp/fake-diagnostics.zip", frame, StringComparison.Ordinal);
    }
}
