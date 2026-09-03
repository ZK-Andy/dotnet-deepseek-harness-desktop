using NetArchTest.Rules;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 架构测试（ADR architecture-mechanization 通道二）：把可机器化的架构规则接进 <c>dotnet test</c>。
/// 校准为**薄桌面壳真实且可强制**的约束（architecture-standards「采纳原理，不照抄模板」）：
/// 组合根（DesktopBootstrap/Program）不被内层依赖、新类型必进 Services/ 子域、子域无真循环
/// （Tray→Update 为单向合理耦合——托盘展示更新状态属应有依赖，反向构成循环才属违规）。
/// 「应用层不得直引具体基础设施实现（R3 边界抽象）与子域互不依赖」属评审面（需接口抽取，超过本
/// 机械化范围），留留评审/AI 兜底，不作硬门禁。NetArchTest 命名空间为前缀匹配，故精确区分根命名空间
/// 须排除 Services 子树。D001–D003 留评审（Roslyn 语义面）。
/// </summary>
public class ArchitectureTests
{
    private const string RootNs = "DeepSeek.Harness.Desktop";
    private const string ServicesNs = "DeepSeek.Harness.Desktop.Services";
    private const string TrayNs = "DeepSeek.Harness.Desktop.Services.Tray";

    private static string Describe(TestResult result) => result.FailingTypes is { Count: > 0 }
        ? string.Join(", ", result.FailingTypes.Select(t => t.FullName ?? t.Name))
        : string.Empty;

    /// <summary>A5 · 新类型必须进 Services/ 或子域：根命名空间只允许组合根（DesktopBootstrap/Program）。
    /// 组合根内的**嵌套编排 token**（ADR composition-root-stage-typing 阶段方法链的类型承诺）属组合根
    /// 一部分，一并放行——该类 token 只在组合根签名间传递、不进 Services/ 子域。</summary>
    [Fact]
    public void TypesResideOnlyInServicesOrComposeRoot()
    {
        IEnumerable<Type> rootTypes = Types.InAssembly(typeof(DesktopBootstrap).Assembly)
            .That().ResideInNamespace(RootNs)
            .And().DoNotResideInNamespace(ServicesNs)
            .GetTypes();
        string[] bad = rootTypes
            .Where(t =>
            {
                if (t.Name.StartsWith("DesktopBootstrap") || t.Name.StartsWith("Program"))
                {
                    return false;
                }

                // 放行嵌套于组合根的编排 token（ADR composition-root-stage-typing）：它们随组合根定义，
                // 语义上属组合根一部分，不构成根命名空间的独立类型。
                if (t.IsNested)
                {
                    Type? declaring = t.DeclaringType;
                    if (declaring is not null && declaring.Name.StartsWith("DesktopBootstrap", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            })
            .Select(t => t.FullName ?? t.Name).ToArray();
        Assert.True(bad.Length == 0,
            $"A5 violate: root-namespace types outside compose root: {string.Join(", ", bad)}");
    }

    /// <summary>A4 · 组合根不被内层依赖（类型级精确）：Services 层不得引用 DesktopBootstrap/Program 类型。</summary>
    [Fact]
    public void ComposeRoot_NotDependedOn_ByServicesLayers()
    {
        TestResult result = Types.InAssembly(typeof(DesktopBootstrap).Assembly)
            .That().ResideInNamespace(ServicesNs)
            .Should().NotHaveDependencyOnAny(
                "DeepSeek.Harness.Desktop.DesktopBootstrap",
                "DeepSeek.Harness.Desktop.Program")
            .GetResult();
        Assert.True(result.IsSuccessful, $"A4 violate: {Describe(result)}");
    }

    /// <summary>A2 · 子域无真循环依赖：Update 子域不得依赖 Tray 子域（Tray→Update 为单向合理耦合）。</summary>
    [Fact]
    public void Subdomains_NoCircularDependency()
    {
        TestResult result = Types.InAssembly(typeof(DesktopBootstrap).Assembly)
            .That().ResideInNamespace("DeepSeek.Harness.Desktop.Services.Update")
            .Should().NotHaveDependencyOn(TrayNs).GetResult();
        Assert.True(result.IsSuccessful, $"A2 violate: {Describe(result)}");
    }
}
