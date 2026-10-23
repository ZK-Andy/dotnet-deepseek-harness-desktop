using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.Update;

/// <summary>新宿主更新面装配（ADR avalonia-g3-update-surface）：机器 → 三条命令路由与托盘
/// 「检查更新」动作的映射。未装载（dev 门禁）时两者同进退，装载时路由覆盖三条命令——与旧宿主同判据。</summary>
public class UpdateSurfaceRegistrationTests
{
    /// <summary>未装载：路由为空、托盘动作为 null；启动入口仍是协调器给的 no-op（不得因缺状态机抛错）。</summary>
    [Fact]
    public void Wire_WithoutMachine_NoRoutersAndNoTrayAction()
    {
        bool started = false;
        UpdateSurface surface = UpdateSurfaceRegistration.Wire(
            machine: null,
            start: () => started = true,
            presenter: NewPresenter(),
            supervisorToken: static () => CancellationToken.None,
            log: _ => { });

        Assert.Empty(surface.Routers);
        Assert.Null(surface.CheckUpdate);

        surface.Start();
        Assert.True(started);
    }

    /// <summary>装载：三条更新命令各有路由承接，托盘动作非 null（菜单项随之出现）。</summary>
    [Fact]
    public void Wire_WithMachine_ExposesThreeUpdateCommandsAndTrayAction()
    {
        UpdateSurface surface = UpdateSurfaceRegistration.Wire(
            machine: NewMachine(),
            start: () => { },
            presenter: NewPresenter(),
            supervisorToken: static () => CancellationToken.None,
            log: _ => { });

        string[] commands = [HostCommandIds.UpdateGetState, HostCommandIds.UpdateCheck, HostCommandIds.UpdateInstall];
        foreach (string command in commands)
        {
            Assert.Contains(surface.Routers, router => router.CanRoute(command));
        }

        Assert.NotNull(surface.CheckUpdate);
        Assert.DoesNotContain(surface.Routers, router => router.CanRoute(HostCommandIds.AutostartGetState));
    }

    private static UpdateCheckPresenter NewPresenter() =>
        new(new FakeWindowScriptTarget(), new PagePump(new RuntimeTimeouts()), new UiLocale(), static () => { }, _ => { });

    private static UpdateStateMachine NewMachine() =>
        new(
            currentVersion: "1.0.0",
            check: _ => Task.FromResult<ReleaseMeta?>(null),
            download: (_, _) => Task.FromResult("/tmp/unused.pkg"),
            install: (_, _, _) => Task.CompletedTask,
            persistence: new InMemoryUpdatePersistence());
}
