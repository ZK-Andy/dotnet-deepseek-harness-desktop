using System.Net;
using System.Net.Sockets;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>桌面运行时血统判据（RuntimeLineage）契约：可证血统才动手，证据不足一律不动（零误杀优先）。</summary>
public class RuntimeLineageTests
{
    private const string Home = "/home/u/.dsh";
    private const string Profile = "dotnet-desktop";

    private static RuntimeLineage.Candidate Candidate(
        int pid,
        string? home = Home,
        string? cmdLine = null,
        DateTimeOffset? startTime = null,
        string token = "token-a") =>
        new(pid, token, home, cmdLine ?? $"node /home/u/.local/bin/dsh --profile {Profile} --port 36111 --no-open", startTime);

    private static RuntimeLineage.Subject RuntimeServer(int pid, DateTimeOffset? startTime) =>
        new(Candidate(pid, startTime: startTime), RuntimeLineage.LineageKind.RuntimeServer);

    private static RuntimeLineage.Subject Helper(int pid, DateTimeOffset? startTime = null) =>
        new(
            Candidate(pid, cmdLine: "node -e const logErr = \"/tmp/dsh-market-restart-x.err.log\"; restart", startTime: startTime),
            RuntimeLineage.LineageKind.MarketRestartHelper);

    /// <summary>父链夹具：显式边之外，pid 1 视为已到 root（0），其余未给边者视为读不到（null）。
    /// 「读不到」与「到 root」是两种不同归属，测试必须能分别表达，故不用"未命中即 0"的简化。</summary>
    private static Func<int, int?> ParentMap(params (int Pid, int Parent)[] edges)
    {
        Dictionary<int, int> map = edges.ToDictionary(edge => edge.Pid, edge => edge.Parent);
        return pid => map.TryGetValue(pid, out int parent) ? parent : pid == 1 ? 0 : null;
    }

    /// <summary>home 不一致（另一 home 的壳实例）不算我方血统：跨实例绝不误杀。</summary>
    [Fact]
    public void Classify_HomeMismatch_IsNone()
    {
        Assert.Equal(
            RuntimeLineage.LineageKind.None,
            RuntimeLineage.Classify(Candidate(42, home: "/home/u/other-home"), Home, Profile));
    }

    /// <summary>本 profile 的 dsh 服务端是本方血统（可被收养）。</summary>
    [Fact]
    public void Classify_RuntimeServerCmdLine_IsRuntimeServer()
    {
        Assert.Equal(
            RuntimeLineage.LineageKind.RuntimeServer,
            RuntimeLineage.Classify(Candidate(42), Home, Profile));
    }

    /// <summary>市场 helper 的 node -e 源码内嵌了被复演的 argv（含 --profile），必须先判为 helper 而非服务端。</summary>
    [Fact]
    public void Classify_MarketHelperMarker_WinsOverEmbeddedProfileArg()
    {
        string helperCmd = "node -e const logErr = \"/tmp/dsh-market-restart-2026-09-11T18-03-28.err.log\"; "
            + $"const args = [\"--profile\",\"{Profile}\",\"--port\",\"36111\"]";
        Assert.Equal(
            RuntimeLineage.LineageKind.MarketRestartHelper,
            RuntimeLineage.Classify(Candidate(42, cmdLine: helperCmd), Home, Profile));
    }

    /// <summary>上游改名后的形状兜底：node -e + restart 仍判 helper——否则它会被当新生服务端收养，而 helper
    /// 拉完替代进程就退出，会让恢复环空转。</summary>
    [Fact]
    public void Classify_NodeDashEHelperShape_IsMarketHelper()
    {
        string renamed = $"node -e const args = [\"--profile\",\"{Profile}\",\"--port\",\"36111\"]; schedule_restart()";
        Assert.Equal(
            RuntimeLineage.LineageKind.MarketRestartHelper,
            RuntimeLineage.Classify(Candidate(42, cmdLine: renamed), Home, Profile));
    }

    /// <summary>无关命令行（不带本 profile）不算血统。</summary>
    [Fact]
    public void Classify_UnrelatedCmdLine_IsNone()
    {
        Assert.Equal(
            RuntimeLineage.LineageKind.None,
            RuntimeLineage.Classify(Candidate(42, cmdLine: "node /home/u/.local/bin/dsh --profile web"), Home, Profile));
    }

    /// <summary>端口冲突签名要求同时点名 EADDRINUSE 与该端口，且端口后不得再接数字（否则 :3611 会命中 :36111）。</summary>
    [Theory]
    [InlineData("Error: listen EADDRINUSE: address already in use 127.0.0.1:36111", 36111, true)]
    [InlineData("Error: listen EADDRINUSE: address already in use 127.0.0.1:36111", 44867, false)]
    [InlineData("Error: listen EADDRINUSE: address already in use 127.0.0.1:36111", 3611, false)]
    [InlineData("dsh web: http://127.0.0.1:36111/?token=x", 36111, false)]
    public void IsPortConflictStderr_RequiresMarkerAndPortBoundary(string line, int port, bool expected)
    {
        Assert.Equal(expected, RuntimeLineage.IsPortConflictStderr(line, port));
    }

    /// <summary>父链归属三态：可达即 Inside，上溯到 root 即 Outside；读不到/自环/超深一律 Unknown（不可证）。</summary>
    [Fact]
    public void ClassifyMembership_ReportsInsideOutsideAndUnknown()
    {
        // 5555 与 1001/1002 的父链都可读（5555 挂在 init 下），才能分别表达「可证在外」与「读不到」
        Func<int, int?> parents = ParentMap((1001, 1000), (1002, 1001), (5555, 1));

        Assert.Equal(RuntimeLineage.TreeMembership.Inside, RuntimeLineage.ClassifyMembership(1000, 1000, parents));
        Assert.Equal(RuntimeLineage.TreeMembership.Inside, RuntimeLineage.ClassifyMembership(1002, 1000, parents));
        Assert.Equal(RuntimeLineage.TreeMembership.Outside, RuntimeLineage.ClassifyMembership(5555, 1000, parents));
        Assert.Equal(RuntimeLineage.TreeMembership.Unknown, RuntimeLineage.ClassifyMembership(1002, 9999, parents));

        // 自环与超深：归属不可证
        Assert.Equal(RuntimeLineage.TreeMembership.Unknown, RuntimeLineage.ClassifyMembership(5, 1, pid => pid));
        Func<int, int?> longChain = pid => pid > 1 ? pid - 1 : 0;
        Assert.Equal(RuntimeLineage.TreeMembership.Unknown, RuntimeLineage.ClassifyMembership(1000, 1, longChain));
    }

    /// <summary>在管运行时及其后代都必须排除：同一次 spawn 的整棵子树共享同一 token，只比 token 会误杀在跑的作业。</summary>
    [Fact]
    public void SelectResidue_ExcludesTrackedRuntimeAndItsDescendants()
    {
        RuntimeLineage.Candidate[] candidates =
        [
            Candidate(1000), Candidate(1001), Candidate(1002), Candidate(1003),
        ];
        // 1001/1002 是 1000 的子孙；1003 挂在 init 下（可证在外）；1000 的父链也必须可读，否则归属不可证
        Func<int, int?> parents = ParentMap((1000, 1), (1001, 1000), (1002, 1001), (1003, 1));

        IReadOnlyList<RuntimeLineage.Subject> residue = RuntimeLineage.SelectResidue(candidates, 1000, Home, Profile, parents);

        Assert.Equal([1003], residue.Select(s => s.Candidate.Pid));
    }

    /// <summary>在管运行时的**祖先**同样不得进残留面：收养场景里市场 helper 正是收养目标的父亲，
    /// 收割它（整树击杀）会连带杀死刚决定收养的运行时。</summary>
    [Fact]
    public void SelectResidue_ExcludesAncestorsOfTrackedRuntime()
    {
        RuntimeLineage.Candidate[] candidates = [Candidate(11294), Candidate(11287, cmdLine: "node -e restart")];
        Func<int, int?> parents = ParentMap((11294, 11287), (11287, 2403), (2403, 1));

        IReadOnlyList<RuntimeLineage.Subject> residue = RuntimeLineage.SelectResidue(candidates, 11294, Home, Profile, parents);

        Assert.Empty(residue);
    }

    /// <summary>归属不可证（父链读不到）同样不进残留面——零误杀优先于收全。</summary>
    [Fact]
    public void SelectResidue_ExcludesUnprovableMembership()
    {
        RuntimeLineage.Candidate[] candidates = [Candidate(11294), Candidate(5000)];
        // 5000 无父链可读 ⇒ Unknown ⇒ 不得当残留收割
        Func<int, int?> parents = ParentMap((11294, 1));

        IReadOnlyList<RuntimeLineage.Subject> residue = RuntimeLineage.SelectResidue(candidates, 11294, Home, Profile, parents);

        Assert.Empty(residue);
    }

    /// <summary>无在管运行时（冷启动）时凡血统可证者皆残留。</summary>
    [Fact]
    public void SelectResidue_WithoutTrackedRuntime_KeepsAllLineageProcesses()
    {
        RuntimeLineage.Candidate[] candidates =
        [
            Candidate(1000),
            Candidate(1001, cmdLine: "node -e /tmp/dsh-market-restart-x.err.log restart"),
        ];

        IReadOnlyList<RuntimeLineage.Subject> residue = RuntimeLineage.SelectResidue(candidates, null, Home, Profile, ParentMap());

        Assert.Equal(2, residue.Count);
        Assert.Contains(residue, s => s.Kind == RuntimeLineage.LineageKind.MarketRestartHelper);
    }

    /// <summary>可证诞生于刚退出那个运行时之后的血统服务端 = 接力续任者 → 收养；无关残留照常进收割面。</summary>
    [Fact]
    public void PlanPortConflict_FreshSuccessor_IsAdopted_AndUnrelatedResidueHarvested()
    {
        var supervisedStart = new DateTimeOffset(2026, 9, 12, 2, 3, 0, TimeSpan.Zero);
        RuntimeLineage.Subject[] residue =
        [
            RuntimeServer(11294, supervisedStart.AddSeconds(29)),
            Helper(7777),
            RuntimeServer(9000, supervisedStart.AddMinutes(-30)),
        ];
        Func<int, int?> parents = ParentMap((11294, 11287), (11287, 2403), (2403, 1), (7777, 1), (9000, 1));

        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(true, supervisedStart, residue, parents);

        Assert.Equal(RuntimeLineage.PortConflictAction.Adopt, plan.Action);
        Assert.Equal(11294, plan.Successor!.Candidate.Pid);
        Assert.Equal([7777, 9000], plan.Harvest.Select(s => s.Candidate.Pid).OrderBy(pid => pid));
    }

    /// <summary>B1 回归：续任者的祖先（市场 helper）绝不能被放进收割面——整树击杀会连带杀死收养目标，
    /// 留下「收养到死 pid + 导航到死端口」。</summary>
    [Fact]
    public void PlanPortConflict_SuccessorAncestor_IsNeverHarvested()
    {
        var supervisedStart = new DateTimeOffset(2026, 9, 12, 2, 3, 0, TimeSpan.Zero);
        RuntimeLineage.Subject[] residue =
        [
            Helper(11287),
            RuntimeServer(11294, supervisedStart.AddSeconds(29)),
        ];
        // helper 11287 是续任者 11294 的父亲（市场 restart.js：helper spawn 替代进程后轮询到其监听）
        Func<int, int?> parents = ParentMap((11294, 11287), (11287, 2403), (2403, 1));

        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(true, supervisedStart, residue, parents);

        Assert.Equal(RuntimeLineage.PortConflictAction.Adopt, plan.Action);
        Assert.Equal(11294, plan.Successor!.Candidate.Pid);
        Assert.Empty(plan.Harvest);
    }

    /// <summary>端口此刻无监听（续任者尚未 bind 或已死）→ 不收养，按残留收割后自己重启，origin 同样不变。</summary>
    [Fact]
    public void PlanPortConflict_FreshSuccessorWithoutListeningPort_IsHarvestedNotAdopted()
    {
        var supervisedStart = new DateTimeOffset(2026, 9, 12, 2, 3, 0, TimeSpan.Zero);
        RuntimeLineage.Subject[] residue = [RuntimeServer(11294, supervisedStart.AddSeconds(29))];

        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(
            portBusy: false,
            supervisedStart,
            residue,
            ParentMap((11294, 1)));

        Assert.Equal(RuntimeLineage.PortConflictAction.HarvestThenRetry, plan.Action);
        Assert.Null(plan.Successor);
        Assert.Equal([11294], plan.Harvest.Select(s => s.Candidate.Pid));
    }

    /// <summary>比刚退出那个运行时更早的血统服务端是既存残留（非接力产物）→ 收割后重试首选端口，不收养。</summary>
    [Fact]
    public void PlanPortConflict_EarlierRuntimeServer_IsHarvestedNotAdopted()
    {
        var supervisedStart = new DateTimeOffset(2026, 9, 12, 2, 3, 0, TimeSpan.Zero);
        RuntimeLineage.Subject[] residue = [RuntimeServer(9000, supervisedStart.AddMinutes(-30))];

        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(
            portBusy: true,
            supervisedStart,
            residue,
            ParentMap((9000, 1)));

        Assert.Equal(RuntimeLineage.PortConflictAction.HarvestThenRetry, plan.Action);
        Assert.Null(plan.Successor);
        Assert.Equal([9000], plan.Harvest.Select(s => s.Candidate.Pid));
    }

    /// <summary>端口被非血统进程占用且无残留 → 回退 OS 分配（漂移告警面）。</summary>
    [Fact]
    public void PlanPortConflict_PortBusyWithoutResidue_FallsBack()
    {
        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(true, null, [], ParentMap());

        Assert.Equal(RuntimeLineage.PortConflictAction.Fallback, plan.Action);
    }

    /// <summary>端口已空且无残留 → 直接重试首选端口（origin 不变）。</summary>
    [Fact]
    public void PlanPortConflict_PortFreeWithoutResidue_RetriesPreferred()
    {
        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(false, null, [], ParentMap());

        Assert.Equal(RuntimeLineage.PortConflictAction.RetryPreferred, plan.Action);
    }

    /// <summary>没有「刚退出那个运行时」的参照时不可证新生 → 一律收割，绝不收养。</summary>
    [Fact]
    public void PlanPortConflict_WithoutSupervisedStart_NeverAdopts()
    {
        RuntimeLineage.Subject[] residue = [RuntimeServer(11294, DateTimeOffset.UtcNow)];

        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(true, null, residue, ParentMap((11294, 1)));

        Assert.Equal(RuntimeLineage.PortConflictAction.HarvestThenRetry, plan.Action);
    }

    /// <summary>候选起始时刻读不到（不可证新生）时同样只收割，不收养。</summary>
    [Fact]
    public void PlanPortConflict_UnknownCandidateStartTime_NeverAdopts()
    {
        RuntimeLineage.Subject[] residue = [RuntimeServer(11294, startTime: null)];

        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(
            portBusy: true,
            supervisedStart: DateTimeOffset.UtcNow.AddMinutes(-1),
            residue,
            ParentMap((11294, 1)));

        Assert.Equal(RuntimeLineage.PortConflictAction.HarvestThenRetry, plan.Action);
    }

    /// <summary>bind 探测：空闲端口判 Free——dsh 此刻 bind 不会立即失败（ADR port-wait-compression）。</summary>
    [Fact]
    public void ProbeLoopbackBind_FreePort_ReturnsFree()
    {
        // 由 OS 分配一个当前空闲的端口：bind 一次拿端口号后释放，再交给被测探测。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Equal(RuntimeLineage.LoopbackBindProbe.Free, RuntimeLineage.ProbeLoopbackBind(port));
    }

    /// <summary>bind 探测：被监听者占住的端口判 Occupied——正是要拦掉的「注定失败尝试」形态。</summary>
    [Fact]
    public void ProbeLoopbackBind_OccupiedPort_ReturnsOccupied()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.Equal(RuntimeLineage.LoopbackBindProbe.Occupied, RuntimeLineage.ProbeLoopbackBind(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Linux 内核只对 LISTEN 态 socket 判 EADDRINUSE：已 bind 未 listen 的占用者探测为 Free——
    /// 固化该平台行为防误判（真实占用者 dsh/占位进程都是监听态，主判据不受影响；非 Linux 不断言）。</summary>
    [Fact]
    public void ProbeLoopbackBind_BoundButNotListening_Free_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 平台行为差异不纳入断言
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.LocalEndPoint!).Port;

        Assert.Equal(RuntimeLineage.LoopbackBindProbe.Free, RuntimeLineage.ProbeLoopbackBind(port));
    }
}
