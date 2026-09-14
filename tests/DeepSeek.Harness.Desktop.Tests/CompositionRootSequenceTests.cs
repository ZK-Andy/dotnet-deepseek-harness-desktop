namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>组合根 Run 主链启动序（批次 0 记序安全网，ADR composition-root-value-flow-pipeline）：
/// 六个 token 阶段（Host/Runtime/Update/App/Supervisor）的产生序已由 token 类型钉进编译器，
/// 无 token 护面的阶段（单实例/profile/随包插件/引导/托盘/后台三服务/主循环）的序仍只活在
/// 源码语句序里——批次 1/2 动主链时此网抓漂移。源序即契约：重排/改名须同步更新本测试。</summary>
public class CompositionRootSequenceTests
{
    /// <summary>Run() 主链调用序按语句序全序断言（带实参调用串因定义签名不同天然不撞；两个无参调用以分号锚定 Run() 内语句而非定义头）。</summary>
    [Fact]
    public void RunChain_StageCalls_AreInContractOrder()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(),
            "src/DeepSeek.Harness.Desktop/DesktopBootstrap.cs"));

        string[] chain =
        [
            "ResolveRuntimeAndDev();",
            "AcquireSingleInstance(preflight)",
            "EnsureDesktopProfile();",
            "SetupHostAndMarker(preflight)",
            "InstallCompanionBeforeSpawn(host)",
            "StartRuntime(host)",
            "InitCloseGateAndUpdateStack(runtime)",
            "BuildApp(runtime, update)",
            "RunBootstrapIfNeeded(app)",
            "ShowTray(app)",
            "SetupSupervisor(app, host)",
            "SetupHealthMonitor(supervisor)",
            "StartUpdateCheck(supervisor)",
            "SharedHomeBannerTask(supervisor)",
            "RunAppLoop(supervisor, host)",
        ];

        int cursor = -1;
        foreach (string call in chain)
        {
            int at = source.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at >= 0, $"Run 主链调用点缺失：{call}");
            Assert.True(at > cursor, $"启动序漂移：{call} 出现在前序阶段之前（契约序 = {string.Join(" → ", chain)}）");
            cursor = at;
        }
    }

    /// <summary>监督器门控依赖 _bootstrapSettled 先创建：RunBootstrapIfNeeded（TCS 诞生地）必须先于
    /// SetupSupervisor（门控闭包读它）——否则门控静默失效（settled 为 null 直接进监视）。</summary>
    [Fact]
    public void RunBootstrapIfNeeded_PrecedesSupervisorGating()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(),
            "src/DeepSeek.Harness.Desktop/DesktopBootstrap.Lifecycle.cs"));
        int created = source.IndexOf("_bootstrapSettled = new TaskCompletionSource", StringComparison.Ordinal);
        int gated = source.IndexOf("BootstrapSettleGate.WaitSettledAsync(_bootstrapSettled", StringComparison.Ordinal);
        Assert.True(created >= 0 && gated >= 0, "门控两端缺失：TCS 创建或握手等待点");
        Assert.True(created < gated, "引导握手失效：SetupSupervisor 的门控早于 _bootstrapSettled 创建");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(typeof(CompositionRootSequenceTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnet-deepseek-harness-desktop.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
