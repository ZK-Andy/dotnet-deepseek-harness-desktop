namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 桌面运行时血统（identity）单一事实源：判定哪些进程属于本壳 spawn 的 dsh 运行时，并给出首选端口
/// 冲突时的处置计划（收养原地接力的续任者 / 收割血统残留 / 回退漂移）。
/// </summary>
/// <remarks>
/// 血统锚点 = 壳 spawn dsh 时注入的 <see cref="TokenEnv"/>（唯一 GUID）。市场自重启的 helper 与它拉起的
/// 续任者由 dsh 以 <c>env: process.env</c> 转发该变量，因而同属血统；dsh 自己的沙箱子进程经
/// <c>scrubbedParentEnv</c> 剥掉全部 <c>DSH_*</c>，不在本判据覆盖内（见 ADR runtime-handoff-adoption 的
/// <c>## Deferred</c> 与 proposed note dsh-sandbox-child-orphan-leak）。
///
/// 判定一律「证据可证才动手」：token 缺失、home 不一致、父链归属不可证（读不到/自环/超深）一律按
/// 「不动」处理——零误杀优先于收全，收不到的残留按端口漂移告警留给人工判读。
/// 生产探针（<c>/proc</c> 读取、进程树杀、回环探活）见 <c>RuntimeLineage.Probes.cs</c>（partial）。
/// </remarks>
public static partial class RuntimeLineage
{
    /// <summary>壳 spawn dsh 时注入的血统 token 环境变量名（唯一 GUID；市场 helper 与续任者继承）。</summary>
    public const string TokenEnv = "DSH_DESKTOP_SPAWN_TOKEN";

    /// <summary>dsh 绑定端口被占时打印的 stderr 签名：它先打签名再悬挂约 40s 才退出，故签名是更早的失败信号。</summary>
    public const string PortConflictMarker = "EADDRINUSE";

    /// <summary>市场自重启 helper 的 cmdline 特征（其 <c>node -e</c> 源码内嵌该日志名前缀）。</summary>
    public const string MarketRestartHelperMarker = "dsh-market-restart-";

    /// <summary>判定父子链时向上回溯的深度上限：超出即按「归属不可证」保守处理。</summary>
    private const int MaxAncestorDepth = 32;

    /// <summary>血统进程快照（<see cref="Enumerate"/> 的产物）。</summary>
    /// <param name="Pid">进程 id。</param>
    /// <param name="Token"><see cref="TokenEnv"/> 的取值——存在即属血统候选。</param>
    /// <param name="Home">该进程 env 的 <c>DSH_HOME</c>（壳 spawn 时写入其生效 home）。</param>
    /// <param name="CmdLine">完整命令行（NUL 已替为空格）。</param>
    /// <param name="StartTime">进程起始时刻；读不到为 null（按「不可证新生」处理）。</param>
    public sealed record Candidate(
        int Pid,
        string Token,
        string? Home,
        string CmdLine,
        DateTimeOffset? StartTime);

    /// <summary>血统类别。</summary>
    public enum LineageKind
    {
        /// <summary>非我方血统（home 不符或命令行形状不符）。</summary>
        None,

        /// <summary>本 profile 的 dsh 服务端：可被收养为在管运行时。</summary>
        RuntimeServer,

        /// <summary>市场自重启 helper：只会催生竞争者，不收养、只收割。</summary>
        MarketRestartHelper,
    }

    /// <summary>血统残留项（<see cref="SelectResidue"/> 的产物）。</summary>
    /// <param name="Candidate">进程快照。</param>
    /// <param name="Kind">血统类别（不会是 <see cref="LineageKind.None"/>）。</param>
    public sealed record Subject(Candidate Candidate, LineageKind Kind);

    /// <summary>进程相对的父链归属（三态：可证在内 / 可证在外 / 不可证）。</summary>
    public enum TreeMembership
    {
        /// <summary>可证位于目标进程子树内。</summary>
        Inside,

        /// <summary>可证位于目标进程子树外（父链已上溯到 root）。</summary>
        Outside,

        /// <summary>不可证（父链读取失败 / 自环 / 超出深度上限）。</summary>
        Unknown,
    }

    /// <summary>首选端口冲突的处置动作。</summary>
    public enum PortConflictAction
    {
        /// <summary>端口已空且无残留：直接重试首选端口（origin 不变）。</summary>
        RetryPreferred,

        /// <summary>新生续任者已占据首选端口：收养它，绝不抢端口。</summary>
        Adopt,

        /// <summary>存在血统残留（或续任者未在服务）：先收割，再重试首选端口。</summary>
        HarvestThenRetry,

        /// <summary>端口被非血统进程占用：回退 OS 分配并写漂移告警。</summary>
        Fallback,
    }

    /// <summary>端口冲突处置计划。</summary>
    /// <param name="Action">本次处置动作。</param>
    /// <param name="Successor">要收养的续任者（仅 <see cref="PortConflictAction.Adopt"/> 非 null）。</param>
    /// <param name="Harvest">要收割的血统残留（可为空；保证不含续任者自身及其祖先）。</param>
    public sealed record PortConflictPlan(
        PortConflictAction Action,
        Subject? Successor,
        IReadOnlyList<Subject> Harvest);

    /// <summary>判定快照的血统类别：home 一致且命令行形状匹配才算我方血统。</summary>
    /// <param name="candidate">进程快照。</param>
    /// <param name="expectedHome">本次生效的 DSH home（<c>HarnessRuntimeHost.ResolveDshHome()</c>）。</param>
    /// <param name="profileName">桌面 profile 名。</param>
    /// <returns>血统类别；证据不足（home 不符/命令行不符）一律 <see cref="LineageKind.None"/>。</returns>
    public static LineageKind Classify(Candidate candidate, string expectedHome, string profileName)
    {
        if (!HomeMatches(candidate.Home, expectedHome))
        {
            return LineageKind.None;
        }

        // 顺序有意：helper 的 node -e 源码内嵌了被复演的 argv（含 --profile），故 helper 必须先判。
        if (IsMarketRestartHelper(candidate.CmdLine))
        {
            return LineageKind.MarketRestartHelper;
        }

        return candidate.CmdLine.Contains($"--profile {profileName}", StringComparison.Ordinal)
            ? LineageKind.RuntimeServer
            : LineageKind.None;
    }

    /// <summary>某条 stderr 行是否是「首选端口被占」签名（EADDRINUSE 且点名该端口，端口后不接数字）。</summary>
    /// <param name="line">子进程 stderr 的一行。</param>
    /// <param name="port">本次尝试的首选端口。</param>
    /// <returns>命中返回 true。</returns>
    /// <remarks>右边界必需：否则 <c>:3611</c> 会命中 <c>:36111</c>，把正常启动的子进程误判为端口冲突。</remarks>
    public static bool IsPortConflictStderr(string line, int port)
    {
        if (!line.Contains(PortConflictMarker, StringComparison.Ordinal))
        {
            return false;
        }

        string needle = $":{port}";
        int index = line.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            int after = index + needle.Length;
            if (after >= line.Length || !char.IsAsciiDigit(line[after]))
            {
                return true;
            }

            index = line.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>从候选快照中筛出血统残留：属我方血统、且不在「在管运行时面」内。</summary>
    /// <param name="candidates">候选快照（通常 <see cref="Enumerate"/> 的产物）。</param>
    /// <param name="trackedPid">在管运行时 pid；无在管运行时传 null（此时凡血统可证者皆残留）。</param>
    /// <param name="expectedHome">本次生效的 DSH home。</param>
    /// <param name="profileName">桌面 profile 名。</param>
    /// <param name="readParentPid">读父进程 id 的探针（注入；生产用 <see cref="ReadParentPid"/>）。</param>
    /// <returns>残留列表；空即无残留。</returns>
    /// <remarks>「在管运行时面」= 在管进程自身、其后代（同一次 spawn 的整棵子树共享同一 token，只比 token
    /// 会把在跑的 MCP/后台作业当残留）、以及它的**祖先**（杀祖先的整树会连带杀死在管运行时，收养场景下
    /// helper 正是收养目标的父亲）。父链归属不可证时同样排除——零误杀优先。</remarks>
    public static IReadOnlyList<Subject> SelectResidue(
        IEnumerable<Candidate> candidates,
        int? trackedPid,
        string expectedHome,
        string profileName,
        Func<int, int?> readParentPid)
    {
        var residue = new List<Subject>();
        foreach (Candidate candidate in candidates)
        {
            LineageKind kind = Classify(candidate, expectedHome, profileName);
            if (kind == LineageKind.None)
            {
                continue;
            }

            if (trackedPid is int tracked && ProtectedByRuntime(candidate.Pid, tracked, readParentPid))
            {
                continue;
            }

            residue.Add(new Subject(candidate, kind));
        }

        return residue;
    }

    /// <summary>给出首选端口冲突的处置计划（纯判定，无副作用）。</summary>
    /// <param name="portBusy">决策时刻首选端口是否仍有回环监听者。</param>
    /// <param name="supervisedStart">刚退出那个在管运行时的起始时刻——「新生续任者」的唯一参照。</param>
    /// <param name="residue">血统残留（<see cref="SelectResidue"/> 的产物）。</param>
    /// <param name="readParentPid">读父进程 id 的探针（用于保证收割面不含续任者自身及其祖先）。</param>
    /// <returns>处置计划。</returns>
    /// <remarks>两个不变量：①只有「端口在服务」且「可证诞生于刚退出那个运行时之后」的血统服务端才被收养——
    /// 参照缺失、起始时刻读不到、或端口此刻无监听（续任者尚未 bind / 已死）都落回收割路径；
    /// ②收养时收割面绝不含续任者自身、其祖先或父链不可证者——helper 是续任者的父亲，整树击杀会连带杀死
    /// 要收养的运行时。</remarks>
    public static PortConflictPlan PlanPortConflict(
        bool portBusy,
        DateTimeOffset? supervisedStart,
        IEnumerable<Subject> residue,
        Func<int, int?> readParentPid)
    {
        var harvest = new List<Subject>();
        var successors = new List<Subject>();
        foreach (Subject subject in residue)
        {
            if (subject.Kind == LineageKind.RuntimeServer && IsBornAfter(subject.Candidate, supervisedStart))
            {
                successors.Add(subject);
                continue;
            }

            harvest.Add(subject);
        }

        if (!portBusy || successors.Count == 0)
        {
            // 端口无监听 = 续任者尚未 bind 或已死：不收养，全部按残留收割后自己重启（origin 同样不变）
            harvest.AddRange(successors);
            return harvest.Count > 0
                ? new PortConflictPlan(PortConflictAction.HarvestThenRetry, null, harvest)
                : new PortConflictPlan(portBusy ? PortConflictAction.Fallback : PortConflictAction.RetryPreferred, null, harvest);
        }

        // 同一次交接只应有一个续任者；多余的新生服务端归入收割面（若它不包含续任者）
        Subject successor = successors.OrderBy(s => s.Candidate.StartTime ?? DateTimeOffset.MinValue).First();
        foreach (Subject subject in successors.Where(s => !ReferenceEquals(s, successor)))
        {
            if (!HarvestKillsSuccessor(subject, successor, readParentPid))
            {
                harvest.Add(subject);
            }
        }

        IReadOnlyList<Subject> safeHarvest = harvest
            .Where(s => !HarvestKillsSuccessor(s, successor, readParentPid))
            .ToList();
        return new PortConflictPlan(PortConflictAction.Adopt, successor, safeHarvest);
    }

    /// <summary>判定 <paramref name="pid"/> 相对 <paramref name="ancestorPid"/> 的父链归属（三态）。</summary>
    /// <param name="pid">待判进程。</param>
    /// <param name="ancestorPid">目标祖先进程（在管运行时或收养目标）。</param>
    /// <param name="readParentPid">读父进程 id 的探针。</param>
    /// <returns>可证在内返回 <see cref="TreeMembership.Inside"/>；父链可读并上溯到 root 返回
    /// <see cref="TreeMembership.Outside"/>；读不到/自环/超深返回 <see cref="TreeMembership.Unknown"/>。</returns>
    public static TreeMembership ClassifyMembership(int pid, int ancestorPid, Func<int, int?> readParentPid)
    {
        if (pid == ancestorPid)
        {
            return TreeMembership.Inside;
        }

        int current = pid;
        for (int depth = 0; depth < MaxAncestorDepth; depth++)
        {
            int? parent = readParentPid(current);
            if (parent is null || parent.Value == current)
            {
                // 父链读不到 / 自环：归属不可证（调用方一律按「不得动」处理）
                return TreeMembership.Unknown;
            }

            if (parent.Value <= 0)
            {
                return TreeMembership.Outside;
            }

            if (parent.Value == ancestorPid)
            {
                return TreeMembership.Inside;
            }

            current = parent.Value;
        }

        return TreeMembership.Unknown;
    }

    /// <summary>候选是否属于「不得收割」的在管运行时面（本文或其后代 / 本文的祖先；不可证同样算）。</summary>
    /// <param name="candidatePid">候选 pid。</param>
    /// <param name="trackedPid">在管运行时 pid。</param>
    /// <param name="readParentPid">读父进程 id 的探针。</param>
    /// <returns>不得收割返回 true。</returns>
    private static bool ProtectedByRuntime(int candidatePid, int trackedPid, Func<int, int?> readParentPid) =>
        ClassifyMembership(candidatePid, trackedPid, readParentPid) != TreeMembership.Outside
        || ClassifyMembership(trackedPid, candidatePid, readParentPid) != TreeMembership.Outside;

    /// <summary>收割该残留是否会连带杀死续任者（续任者落在其子树内，或父链不可证）。</summary>
    /// <param name="subject">候选残留。</param>
    /// <param name="successor">收养目标。</param>
    /// <param name="readParentPid">读父进程 id 的探针。</param>
    /// <returns>会连带杀死返回 true（该残留必须从收割面剔除）。</returns>
    private static bool HarvestKillsSuccessor(Subject subject, Subject successor, Func<int, int?> readParentPid) =>
        ClassifyMembership(successor.Candidate.Pid, subject.Candidate.Pid, readParentPid) != TreeMembership.Outside;

    /// <summary>命令行是否形如市场自重启 helper。</summary>
    /// <param name="cmdLine">完整命令行。</param>
    /// <returns>形如 helper 返回 true。</returns>
    /// <remarks>两个判据：上游现形的重启日志名前缀（<see cref="MarketRestartHelperMarker"/>），以及
    /// <c>node -e</c> + <c>restart</c> 的形状兜底——上游若改名，helper 会因内嵌 argv 含 <c>--profile</c>
    /// 被误判成 <see cref="LineageKind.RuntimeServer"/> 并可证新生，从而被**收养**为一个马上退出的进程。
    /// 兜底只放宽「收割」（不动在管运行时），是刻意保留的改名韧性。</remarks>
    private static bool IsMarketRestartHelper(string cmdLine) =>
        cmdLine.Contains(MarketRestartHelperMarker, StringComparison.Ordinal)
        || (cmdLine.Contains("node -e ", StringComparison.Ordinal) && cmdLine.Contains("restart", StringComparison.OrdinalIgnoreCase));

    /// <summary>候选是否可证诞生于 <paramref name="supervisedStart"/> 之后（即接力续任者，而非更早的残留实例）。</summary>
    /// <param name="candidate">候选快照。</param>
    /// <param name="supervisedStart">刚退出那个运行时的起始时刻。</param>
    /// <returns>可证更新返回 true。</returns>
    private static bool IsBornAfter(Candidate candidate, DateTimeOffset? supervisedStart) =>
        supervisedStart is not null
        && candidate.StartTime is not null
        && candidate.StartTime.Value > supervisedStart.Value;

    /// <summary>home 比对：两侧去尾分隔符后按平台大小写语义比较；任一为空即不匹配。</summary>
    /// <param name="candidateHome">候选进程 env 里的 home。</param>
    /// <param name="expectedHome">本次生效 home。</param>
    /// <returns>指向同一 home 返回 true。</returns>
    private static bool HomeMatches(string? candidateHome, string expectedHome)
    {
        if (string.IsNullOrWhiteSpace(candidateHome) || string.IsNullOrWhiteSpace(expectedHome))
        {
            return false;
        }

        string left = Path.TrimEndingDirectorySeparator(candidateHome);
        string right = Path.TrimEndingDirectorySeparator(expectedHome);
        return string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
