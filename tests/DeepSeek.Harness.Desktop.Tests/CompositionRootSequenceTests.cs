using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>组合根 Run 主链启动序（批次 0 记序安全网，ADR composition-root-value-flow-pipeline）：
/// 阶段产出类型（批次 2 起：Preflight/HostSetup/RuntimeSetup/UpdateSetup/AppSetup/SupervisorSetup）
/// 把产生序钉进编译器——消费段收参数、缺前置产出即缺值编译失败（编译期面无法在测试里断言，
/// 由源序与产出形态两网兜底）。源序即契约：重排/改名须同步更新本测试。</summary>
public class CompositionRootSequenceTests
{
    /// <summary>Run() 主链调用序按语句序全序断言（带实参调用串因定义签名不同天然不撞；无参调用以分号锚定 Run() 内语句而非定义头）。</summary>
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
            "InstallCompanionBeforeSpawn(preflight, host)",
            "StartRuntime(preflight, host)",
            "InitCloseGateAndUpdateStack(preflight, runtime)",
            "BuildApp(preflight, runtime, update)",
            "RunBootstrapIfNeeded(preflight, app)",
            "ShowTray(app)",
            "SetupSupervisor(preflight, app, host)",
            "SetupHealthMonitor(app, supervisor)",
            "StartUpdateCheck(update)",
            "SharedHomeBannerTask(preflight, app, host, supervisor)",
            "RunAppLoop(preflight, app, supervisor)",
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

    /// <summary>值流形态（批次 2）：空载荷 token 退役，六个阶段产出类型均携带真实值，且**每个
    /// 产出属性至少有一处消费点**——产出即执行序证明，空载荷或死载荷都会退回「只钉序不传值」的旧形态。</summary>
    [Fact]
    public void StageOutputs_CarryConsumedValues()
    {
        string dir = Path.Combine(RepoRoot(), "src/DeepSeek.Harness.Desktop");
        string source = File.ReadAllText(Path.Combine(dir, "DesktopBootstrap.cs"));

        Assert.False(
            Regex.IsMatch(source, @"record struct \w+Token\s*;"),
            "空载荷 token 形态应已退役（值流管线批次 2）");

        // 消费点可落在任一分部（App/Lifecycle/Navigation），故按整个组合根分部集扫描；消费须锚定到
        // 阶段产出变量/形参名（Run 主链与各消费段共用同名 preflight/host/runtime/update/app/supervisor），
        // 否则无关同名成员（arrived.Task、文档注释里的 DesktopBootstrap.App.cs）会误判为已消费。
        string composed = string.Concat(Directory.GetFiles(dir, "DesktopBootstrap*.cs")
            .OrderBy(p => p, StringComparer.Ordinal).Select(File.ReadAllText));
        const string stageVariables = "preflight|host|runtime|update|app|supervisor";

        string[] outputs = ["Preflight", "HostSetup", "RuntimeSetup", "UpdateSetup", "AppSetup", "SupervisorSetup"];
        foreach (string output in outputs)
        {
            // 声明以 ");" 收尾；载荷取到分号为止，容忍参数类型里的 ')'（如元组）。
            Match declared = Regex.Match(source, $@"private readonly record struct {output}\((?<payload>[^;]*)\);");
            Assert.True(declared.Success, $"阶段产出类型缺失：{output}");
            string payload = declared.Groups["payload"].Value.Trim();
            Assert.False(payload.Length == 0, $"阶段产出无载荷：{output}（值流要求返回真实值）");

            foreach (string parameter in SplitTopLevel(payload))
            {
                Match name = Regex.Match(parameter.Trim(), @"([A-Za-z_]\w*)$");
                Assert.True(name.Success, $"阶段产出参数无法解析：{output}({parameter})");
                string property = name.Groups[1].Value;
                Assert.True(
                    Regex.IsMatch(composed, $@"\b(?:{stageVariables})\.{property}\b"),
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
        string source = File.ReadAllText(Path.Combine(RepoRoot(),
            "src/DeepSeek.Harness.Desktop.Infrastructure/Services/FirstBootBootstrapService.cs"));
        int created = source.IndexOf("_settled = new TaskCompletionSource", StringComparison.Ordinal);
        int started = source.IndexOf("Task.Run(() => RunAsync", StringComparison.Ordinal);
        Assert.True(created >= 0 && started >= 0, "门控两端缺失：落定句柄创建或引导任务启动点");
        Assert.True(created < started, "引导握手失效：引导任务启动早于落定句柄创建");
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
