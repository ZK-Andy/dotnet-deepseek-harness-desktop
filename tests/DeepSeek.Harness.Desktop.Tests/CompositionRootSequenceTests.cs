using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>启动序安全网（ADR compose-root-form-separation 后的形态）：组合根只装配与触发
/// （容器前头部 + BuildApp 装配 + 编排触发），阶段主链与阶段产出搬进 <c>Bootstrap.StartupSequence</c>。
/// 阶段产出类型（Preflight/HostSetup/UpdateSetup/AppSetup，正式类型见 Bootstrap/StartupStages.cs；
/// 监督任务以局部 Task 流转、不设阶段产出，ADR sequencer-stage-value-retirement）
/// 把产生序钉进编译器——消费段收参数、缺前置产出即缺值编译失败（编译期面无法在测试里断言，
/// 由源序与产出形态两网兜底）。源序即契约：重排/改名须同步更新本测试。</summary>
public class CompositionRootSequenceTests
{
    /// <summary>组合根容器前头部调用序按语句序全序断言（单实例早退、代理启动留根，编排两相触发收尾：
    /// 唯一入口桥 + 同步相）。</summary>
    [Fact]
    public void ComposeRoot_PreContainerChain_IsInContractOrder()
    {
        string source = File.ReadAllText(Path.Combine(TestRepoRoot.Find(),
            "src/DeepSeek.Harness.Desktop/DesktopAdapter.cs"));

        string[] chain =
        [
            "WebkitSandboxFallback.Apply();",
            "PreBuildRegistration.CreateSharedState();",
            "PreBuildRegistration.CreateShellResources();",
            "resources.Attach(supervisorCts);",
            "PreBuildRegistration.CreatePreflight(",
            "AcquireSingleInstance(preflight, () => builtApp,",
            "resources.Attach(instanceListener);",
            "StartProxy(shared.ShellForward);",
            "resources.Attach(proxy);",
            "PreBuildRegistration.CreateUpdateAndTrayAssembly(",
            "BuildApp(preflight, proxy.Proxy, update, tray,",
            "ActivatorUtilities.CreateInstance<Bootstrap.StartupSequence>(builtApp.Services, app)",
            "sequence.RunStartupAsync().GetAwaiter().GetResult();",
            "return sequence.RunMainLoop();",
            "resources.Dispose();",
        ];

        AssertChainOrder(source, chain);
    }

    /// <summary>编排服务两相主链调用序按语句序全序断言（搬迁后阶段链的唯一家；ADR entry-bridge-two-phase
    /// 把 <c>Run</c> 切成异步相 <c>RunStartupAsync</c>（I/O 起步链）与同步相 <c>RunMainLoop</c>（Ryn 面尾段
    /// + 主循环），本测试按相位锚定顺序）。</summary>
    [Fact]
    public void StartupSequence_RunChain_StageCalls_AreInContractOrder()
    {
        string source = File.ReadAllText(Path.Combine(TestRepoRoot.Find(),
            "src/DeepSeek.Harness.Desktop/Bootstrap/StartupSequence.cs"));

        string[] chain =
        [
            "public async Task RunStartupAsync()",
            "_resolutions.Profile.EnsureReady();",
            "HostSetup host = _resolutions.Host.Value;",
            "_resolutions.Companion.EnsureInstalled(",
            "_runtimeStarter.StartAsync(host.Host, CancellationToken.None).ConfigureAwait(false)",
            "public int RunMainLoop()",
            "RunBootstrapIfNeeded();",
            "ShowTray();",
            "SetupSupervisorAsync(host)",
            "SetupHealthMonitor();",
            "InstallCaptionBar();",
            "StartUpdateCheck();",
            "StartupNoticeTask();",
            "return _exit.Run(",
        ];

        AssertChainOrder(source, chain);
    }

    /// <summary>铸币态失效接线钉（ADR mint-epoch-mux-gate）：失败面承载即恢复周期起点（子进程退出后、
    /// 重启等待前），监督用例必须在此失效铸币态——删除该调用则重启窗口的页面自刷绕过 holder 门控，
    /// 落进指向已死进程的 502/半成品页。mutation 反证：删掉 Invalidate() 调用本测即红。
    /// （ADR 组合根机制收官 step-1 后失败面与恢复回调住 Core.Bootstrap.RuntimeSupervision。）</summary>
    [Fact]
    public void RuntimeSupervision_RecoveryStart_InvalidatesMintRoute()
    {
        string source = File.ReadAllText(Path.Combine(TestRepoRoot.Find(),
            "src/DeepSeek.Harness.Desktop.Core/Bootstrap/RuntimeSupervision.cs"));

        int anchor = source.IndexOf("private ValueTask ShowFailureSurfaceAsync(", StringComparison.Ordinal);
        Assert.True(anchor >= 0, "失败面入口未找到（ShowFailureSurfaceAsync 被重排？）");
        Assert.True(
            source.IndexOf("_mintEpoch.Invalidate()", anchor, StringComparison.Ordinal) > anchor,
            "恢复周期起点必须失效铸币态（dsh 死即 holder 门控重武装）");
    }

    /// <summary>窗口几何持久化接线钉（rationale 见 ADR window-geometry-ryn-native-persist）。
    /// mutation 反证：删掉 PersistWindowState 赋值（或注释失效）本测即红。</summary>
    [Fact]
    public void BuildApp_EnablesRynWindowStatePersistence()
    {
        string source = File.ReadAllText(Path.Combine(TestRepoRoot.Find(),
            "src/DeepSeek.Harness.Desktop/RynOptionsProfile.cs"));

        Assert.Contains("opts.PersistWindowState = true;", source, StringComparison.Ordinal);
        // 注释失效同红（R2 评审建议）：源锚只防删除不防注释，补一发防注释锚
        Assert.DoesNotContain("// opts.PersistWindowState", source, StringComparison.Ordinal);
    }

    private static void AssertChainOrder(string source, string[] chain)
    {
        int cursor = -1;
        foreach (string call in chain)
        {
            int at = source.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at >= 0, $"主链调用点缺失：{call}");
            Assert.True(at > cursor, $"启动序漂移：{call} 出现在前序阶段之前（契约序 = {string.Join(" → ", chain)}）");
            cursor = at;
        }
    }

    /// <summary>值流形态：阶段产出类型均为携带真实值的正式类型，且**每个产出属性至少有一处消费点**——
    /// 产出即执行序证明，空载荷或死载荷都会退回「只钉序不传值」的旧形态。</summary>
    [Fact]
    public void StageOutputs_CarryConsumedValues()
    {
        string dir = Path.Combine(TestRepoRoot.Find(), "src/DeepSeek.Harness.Desktop");
        string stages = File.ReadAllText(Path.Combine(dir, "Bootstrap", "StartupStages.cs"));

        Assert.False(
            Regex.IsMatch(stages, @"record struct \w+Token\s*[;({]"),
            "空载荷 token 形态应已退役（值流管线批次 2）");

        // 消费点可落在编排服务任一分部、组合根分部或注册装配处（ADR 组合根机制收官终局的 B3 把顶栏注入
        // 与启动期告知用例移进注册处），故按三侧文件集扫描；消费须锚定阶段产出变量/形参名（同名
        // preflight/host/update/app，编排服务内为 _ 前缀），否则无关同名成员会误判为已消费。
        string[] sources = Directory.GetFiles(dir, "DesktopAdapter*.cs")
            .Concat(Directory.GetFiles(Path.Combine(dir, "Bootstrap"), "StartupSequence*.cs"))
            .Concat(Directory.GetFiles(Path.Combine(dir, "Bootstrap"), "StartupUseCaseRegistration.cs"))
            .Concat(Directory.GetFiles(Path.Combine(dir, "Bootstrap"), "PreBuildRegistration.cs"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToArray();
        string composed = string.Concat(sources);
        const string stageVariables = "_?(?:preflight|host|updates?|app)";

        string[] outputs = ["Preflight", "HostSetup", "UpdateSetup", "AppSetup"];
        foreach (string output in outputs)
        {
            // 声明以 ");" 收尾；载荷取到分号为止，容忍参数类型里的 ')'（如元组）。
            Match declared = Regex.Match(stages, $@"internal readonly record struct {output}\((?<payload>[^;]*)\);");
            Assert.True(declared.Success, $"阶段产出类型缺失：{output}");
            string payload = declared.Groups["payload"].Value.Trim();
            Assert.False(payload.Length == 0, $"阶段产出无载荷：{output}（值流要求返回真实值）");

            foreach (string parameter in SplitTopLevel(payload))
            {
                Match name = Regex.Match(parameter.Trim(), @"([A-Za-z_]\w*)$");
                Assert.True(name.Success, $"阶段产出参数无法解析：{output}({parameter})");
                string property = name.Groups[1].Value;
                Assert.True(
                    Regex.IsMatch(composed, $@"(?<![A-Za-z_0-9]){stageVariables}\.{property}\b"),
                    $"阶段产出属性无消费点（死载荷）：{output}.{property}（消费须经阶段产出变量 {stageVariables}）");
            }
        }
    }

    /// <summary>按顶层逗号切分参数表——忽略 &lt;&gt;/()/[] 内的逗号，容忍元组/泛型实参。</summary>
    private static IEnumerable<string> SplitTopLevel(string parameters)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            char c = parameters[i];
            if (c is '<' or '(' or '[')
            {
                depth++;
            }
            else if (c is '>' or ')' or ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return parameters[start..i];
                start = i + 1;
            }
        }

        yield return parameters[start..];
    }

    /// <summary>监督器门控依赖引导落定句柄先创建：批次 1 把 TCS 私有化进引导服务后，句柄诞生点与
    /// 门控解耦，该序仍须钉住——引导任务先跑就读不到自己的落定句柄（门控静默失效，直接进监视）。</summary>
    [Fact]
    public void SettleHandle_CreatedBeforeBootstrapTaskStarts()
    {
        string source = File.ReadAllText(Path.Combine(TestRepoRoot.Find(),
            "src/DeepSeek.Harness.Desktop.Infrastructure/Bootstrap/FirstBootBootstrapService.cs"));
        int created = source.IndexOf("_settled = new TaskCompletionSource", StringComparison.Ordinal);
        int started = source.IndexOf("Task.Run(() => RunAsync", StringComparison.Ordinal);
        Assert.True(created >= 0 && started >= 0, "门控两端缺失：落定句柄创建或引导任务启动点");
        Assert.True(created < started, "引导握手失效：引导任务启动早于落定句柄创建");
    }
}
