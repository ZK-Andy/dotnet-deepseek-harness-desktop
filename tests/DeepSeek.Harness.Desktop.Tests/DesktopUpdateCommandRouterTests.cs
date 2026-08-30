using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>DesktopUpdateCommandRouter 的后台 token 契约（回归 R2#5）：check/install 的长任务
/// 挂 backgroundToken（宿主监督器），绝不复用 IPC 请求作用域 token——请求帧返回后分发器
/// 若取消请求 token，分钟级下载/兜底强退会被连坐。</summary>
public class DesktopUpdateCommandRouterTests
{
    private sealed class FakePersistence : UpdateStateMachine.IPersistence
    {
        public UpdateStateMachine.ReadyRecord? Record { get; set; }
        public Task<UpdateStateMachine.ReadyRecord?> GetAsync(CancellationToken ct) => Task.FromResult(Record);
        public Task SetAsync(UpdateStateMachine.ReadyRecord record, CancellationToken ct)
        {
            Record = record;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct)
        {
            Record = null;
            return Task.CompletedTask;
        }
    }

    private static UpdateStateMachine CreateMachine(Action<CancellationToken> onCheck)
    {
        return new UpdateStateMachine(
            currentVersion: "0.3.11",
            check: ct =>
            {
                onCheck(ct);
                return Task.FromResult<ReleaseMeta?>(null);
            },
            download: (_, _) => Task.FromResult("/tmp/unused.deb"),
            install: (_, _, _) => Task.CompletedTask,
            persistence: new FakePersistence());
    }

    /// <summary>验证 check 后台任务收到 backgroundToken() 令牌而非请求作用域 token，请求 token 预取消也不连坐。</summary>
    [Fact]
    public async Task Check_PassesBackgroundToken_ToMachine()
    {
        // 后台任务必须拿到 backgroundToken() 的令牌（监督器），而非请求作用域 token
        CancellationToken? seen = null;
        var requestCts = new CancellationTokenSource();
        requestCts.Cancel(); // 恶劣前提：请求 token 已取消——后台任务不受它连坐
        using var backgroundCts = new CancellationTokenSource();
        UpdateStateMachine machine = CreateMachine(ct => seen = ct);
        var router = new DesktopUpdateCommandRouter(machine, backgroundToken: () => backgroundCts.Token);

        await router.RouteAsync("desktop.update.check", ReadOnlyMemory<byte>.Empty, null!, requestCts.Token);

        await Task.Delay(50); // check 是 Task.Run 后台任务，让一拍
        Assert.NotNull(seen);
        Assert.False(seen.Value.IsCancellationRequested);
        Assert.Equal(backgroundCts.Token, seen.Value);
    }

    /// <summary>验证 backgroundToken 未注入时以 CancellationToken.None 按不可取消处理，不退回已取消的请求 token。</summary>
    [Fact]
    public async Task Check_WithoutBackgroundToken_NotCancellable()
    {
        // backgroundToken 未注入（测试形态）时按不可取消处理，而非退回请求 token
        CancellationToken? seen = null;
        var requestCts = new CancellationTokenSource();
        requestCts.Cancel();
        UpdateStateMachine machine = CreateMachine(ct => seen = ct);
        var router = new DesktopUpdateCommandRouter(machine);

        await router.RouteAsync("desktop.update.check", ReadOnlyMemory<byte>.Empty, null!, requestCts.Token);

        await Task.Delay(50);
        Assert.NotNull(seen);
        Assert.Equal(CancellationToken.None, seen.Value);
    }

    /// <summary>验证 install 长任务经持久化恢复 ready 后同样收到 backgroundToken 令牌，而非已取消的请求 token。</summary>
    [Fact]
    public async Task Install_PassesBackgroundToken_ToMachine()
    {
        // install 兜底强退同样是长任务：先经持久化记录把状态机带进 ready，再断言 install 收到的是 backgroundToken
        var requestCts = new CancellationTokenSource();
        requestCts.Cancel(); // 恶劣前提：请求 token 已取消——install 不受它连坐
        using var backgroundCts = new CancellationTokenSource();
        CancellationToken? seen = null;
        string path = Path.Combine(Path.GetTempPath(), $"upd-{Guid.NewGuid():N}.deb");
        File.WriteAllBytes(path, [1]);
        var persist = new FakePersistence();
        await persist.SetAsync(new UpdateStateMachine.ReadyRecord("0.3.12", path), CancellationToken.None);
        var machine = new UpdateStateMachine(
            currentVersion: "0.3.11",
            check: _ => throw new InvalidOperationException("不应触发网络检查"),
            download: (_, _) => Task.FromResult(path),
            install: (_, _, ct) =>
            {
                seen = ct;
                return Task.CompletedTask;
            },
            persistence: persist);
        await machine.StartAsync(CancellationToken.None); // 从持久化恢复 ready
        Assert.Equal(UpdateStatus.Ready, machine.State.Status);

        var router = new DesktopUpdateCommandRouter(machine, backgroundToken: () => backgroundCts.Token);
        await router.RouteAsync("desktop.update.install", ReadOnlyMemory<byte>.Empty, null!, requestCts.Token);

        Assert.NotNull(seen);
        Assert.Equal(backgroundCts.Token, seen.Value);
    }
}
