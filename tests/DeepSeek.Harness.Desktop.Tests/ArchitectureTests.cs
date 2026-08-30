using NetArchTest.Rules;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 架构测试（ADR architecture-mechanization 通道二）：把可机器化的架构规则接进 <c>dotnet test</c>。
/// NetArchTest 的命名空间是**前缀匹配**，故精确区分根命名空间须排除 Services 子树。
/// D001–D003 留评审（Roslyn 语义面，见根 AGENTS 评审检查项）。
/// </summary>
public class ArchitectureTests
{
    private const string RootNs = "DeepSeek.Harness.Desktop";
    private const string ServicesNs = "DeepSeek.Harness.Desktop.Services";
    private const string UpdateNs = "DeepSeek.Harness.Desktop.Services.Update";
    private const string TrayNs = "DeepSeek.Harness.Desktop.Services.Tray";

    // AppJsonContext 是跨命名空间的帧序列化上下文聚合器（AOT 源生成），它列出全部帧类型属
    // 其本职，容许其依赖子域帧。其余 Services 根类型不得反向依赖子域（A1）。
    private static readonly string[] s_a1Allow = { "AppJsonContext" };

    private static string Describe(TestResult result) => result.FailingTypes is { Count: > 0 }
        ? string.Join(", ", result.FailingTypes.Select(t => t.FullName ?? t.Name))
        : string.Empty;

    /// <summary>A5 · 新类型必须进 Services/ 或子域：根命名空间只允许组合根（DesktopBootstrap/Program）。</summary>
    [Fact]
    public void TypesResideOnlyInServicesOrComposeRoot()
    {
        IEnumerable<Type> rootTypes = Types.InAssembly(typeof(DesktopBootstrap).Assembly)
            .That().ResideInNamespace(RootNs)
            .And().DoNotResideInNamespace(ServicesNs)
            .GetTypes();
        string[] bad = rootTypes
            .Where(t => !(t.Name.StartsWith("DesktopBootstrap") || t.Name.StartsWith("Program")))
            .Select(t => t.FullName ?? t.Name).ToArray();
        Assert.True(bad.Length == 0,
            $"A5 violate: root-namespace types outside compose root: {string.Join(", ", bad)}");
    }

    /// <summary>A1 · Services 根（非子域）不得依赖 Update/Tray 子域（子域自成一体，经接口对外）。</summary>
    [Fact]
    public void ServicesRoot_DoNotDependOn_Subdomains()
    {
        PredicateList servicesRoot = Types.InAssembly(typeof(DesktopBootstrap).Assembly)
            .That().ResideInNamespace(ServicesNs)
            .And().DoNotResideInNamespace(UpdateNs)
            .And().DoNotResideInNamespace(TrayNs)
            .And().DoNotHaveName(s_a1Allow);
        TestResult update = servicesRoot.Should().NotHaveDependencyOn(UpdateNs).GetResult();
        TestResult tray = servicesRoot.Should().NotHaveDependencyOn(TrayNs).GetResult();
        Assert.True(update.IsSuccessful && tray.IsSuccessful,
            $"A1 violate: update={Describe(update)} tray={Describe(tray)}");
    }

    /// <summary>A2 · 禁子域间循环依赖：Update 与 Tray 两个子域互不依赖（各自经接口对外）。</summary>
    [Fact]
    public void NoCircularDependency_BetweenSubdomains()
    {
        TestResult update = Types.InAssembly(typeof(DesktopBootstrap).Assembly)
            .That().ResideInNamespace(UpdateNs)
            .Should().NotHaveDependencyOn(TrayNs).GetResult();
        TestResult tray = Types.InAssembly(typeof(DesktopBootstrap).Assembly)
            .That().ResideInNamespace(TrayNs)
            .Should().NotHaveDependencyOn(UpdateNs).GetResult();
        Assert.True(update.IsSuccessful && tray.IsSuccessful,
            $"A2 violate update={Describe(update)} tray={Describe(tray)}");
    }

    /// <summary>A4 · 组合根不被内层依赖：Services 类型不反向引用 DesktopBootstrap/Program 类型。</summary>
    [Fact]
    public void ComposeRoot_NotDependedOn_ByServicesLayers()
    {
        // 组合根类型（DesktopBootstrap / Program）是根命名空间的隔离成员；Services 层引用它即
        // 破坏「组合根只装配、下层被装配」的方向。类型级依赖用全名精确表达（避免前缀误报）。
        TestResult result = Types.InAssembly(typeof(DesktopBootstrap).Assembly)
            .That().ResideInNamespace(ServicesNs)
            .Should().NotHaveDependencyOnAny(
                "DeepSeek.Harness.Desktop.DesktopBootstrap",
                "DeepSeek.Harness.Desktop.Program")
            .GetResult();
        Assert.True(result.IsSuccessful, $"A4 violate: {Describe(result)}");
    }

    /// <summary>A3 · 边界实现类型不得被非组合根（应用层）直接实例化。</summary>
    [Fact]
    public void BoundaryComponents_NotUsedBy_NonComposeRoot()
    {
        // 边界 = 与外部世界交互的组件（进程/下载/网络/文件系统）。应用层类型（Services 非边界）
        // 不得依赖它们；组合根（DesktopBootstrap*）与边界自身除外。
        string[] boundary = {
            "DeepSeek.Harness.Desktop.Services.HarnessRuntimeHost",
            "DeepSeek.Harness.Desktop.Services.RuntimeBootstrap",
            "DeepSeek.Harness.Desktop.Services.MarketInstallHelper",
            "DeepSeek.Harness.Desktop.Services.InstallerDownloader",
            "DeepSeek.Harness.Desktop.Services.ReleaseMetaClient",
            "DeepSeek.Harness.Desktop.Services.CliShimRegistrar",
            "DeepSeek.Harness.Desktop.Services.SystemBrowser",
            "DeepSeek.Harness.Desktop.Services.RuntimeLocator",
        };
        TestResult result = Types.InAssembly(typeof(DesktopBootstrap).Assembly)
            .That().ResideInNamespace(ServicesNs)
            .And().DoNotResideInNamespace(UpdateNs)
            .And().DoNotResideInNamespace(TrayNs)
            .And().DoNotHaveNameStartingWith("RuntimeBootstrap")
            .And().DoNotHaveNameStartingWith("HarnessRuntimeHost")
            .And().DoNotHaveNameStartingWith("MarketInstallHelper")
            .Should().NotHaveDependencyOnAny(boundary)
            .GetResult();
        Assert.True(result.IsSuccessful, $"A3 violate: {Describe(result)}");
    }
}
