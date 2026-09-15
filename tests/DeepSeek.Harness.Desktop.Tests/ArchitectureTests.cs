using System.Reflection;
using System.Xml.Linq;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 架构测试（B4 升级形态，ADR official-clean-architecture-adoption）：A2/A4/A5 命名空间前缀门
/// 退役，升级为**项目引用断言**——依赖方向（R2）由编译器在 csproj 层强制，本测试断言引用图
/// 形状（csproj 声明 + 编译产物程序集引用两级互证）：Core 零项目引用、Infrastructure 只引 Core、
/// 主工程（Presentation/组合根）引 Core + Infrastructure，测试工程按层镜像。D001–D003 留评审。
/// </summary>
public class ArchitectureTests
{
    private static string[] ProjectReferences(string csprojRelative)
    {
        string path = Path.Combine(TestRepoRoot.Find(), csprojRelative);
        Assert.True(File.Exists(path), $"missing csproj: {csprojRelative}");
        return XDocument.Load(path).Descendants("ProjectReference")
            .Select(e => Path.GetRelativePath(TestRepoRoot.Find(),
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!,
                    ((string?)e.Attribute("Include") ?? string.Empty).Replace('\\', '/')))))
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    private static string[] AssemblyReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).Where(n => n.Length > 0).ToArray();

    /// <summary>csproj 断言：Core 零项目引用（Application Core 零外层依赖，官方判据 2）。</summary>
    [Fact]
    public void CoreProjectReference_IsEmpty()
    {
        string[] refs = ProjectReferences("src/DeepSeek.Harness.Desktop.Core/DeepSeek.Harness.Desktop.Core.csproj");
        Assert.True(refs.Length == 0, $"R2 violate: Core must have zero ProjectReference, got: {string.Join(", ", refs)}");
    }

    /// <summary>csproj 断言：Infrastructure 项目引用必须恰为 Core（R2 编译器强制的声明面互证）。</summary>
    [Fact]
    public void InfrastructureReferences_OnlyCore()
    {
        string[] refs = ProjectReferences("src/DeepSeek.Harness.Desktop.Infrastructure/DeepSeek.Harness.Desktop.Infrastructure.csproj");
        string[] expected = ["src/DeepSeek.Harness.Desktop.Core/DeepSeek.Harness.Desktop.Core.csproj"];
        Assert.Equal(expected, refs);
    }

    /// <summary>csproj 断言：主工程（Presentation/组合根）项目引用恰为 Core + Infrastructure。</summary>
    [Fact]
    public void PresentationReferences_CoreAndInfrastructure()
    {
        string[] refs = ProjectReferences("src/DeepSeek.Harness.Desktop/DeepSeek.Harness.Desktop.csproj");
        string[] expected =
        [
            "src/DeepSeek.Harness.Desktop.Core/DeepSeek.Harness.Desktop.Core.csproj",
            "src/DeepSeek.Harness.Desktop.Infrastructure/DeepSeek.Harness.Desktop.Infrastructure.csproj",
        ];
        Assert.Equal(expected, refs);
    }

    /// <summary>程序集级互证：Core 编译产物零 DeepSeek*/Ryn* 引用（R2 种子测试升级保留面）。</summary>
    [Fact]
    public void CoreAssembly_HasZeroOuterReferences()
    {
        Assembly core = typeof(DeepSeek.Harness.Desktop.Core.Update.UpdateStateMachine).Assembly;
        string self = core.GetName().Name ?? string.Empty;
        string[] bad = AssemblyReferences(core)
            .Where(n => n != self &&
                (n.StartsWith("DeepSeek.Harness.Desktop", StringComparison.Ordinal) || n.StartsWith("Ryn", StringComparison.Ordinal)))
            .ToArray();
        Assert.True(bad.Length == 0, $"R2 violate: Core assembly references outer assemblies: {string.Join(", ", bad)}");
    }

    /// <summary>程序集级互证：Infrastructure 编译产物只许引用 Core（Ryn*/Presentation 引用即违 R2）。</summary>
    [Fact]
    public void InfrastructureAssembly_HasZeroPresentationReferences()
    {
        Assembly infra = typeof(DeepSeek.Harness.Desktop.Infrastructure.Runtime.HarnessRuntimeHost).Assembly;
        string[] bad = AssemblyReferences(infra)
            .Where(n => n.StartsWith("Ryn", StringComparison.Ordinal) ||
                (n.StartsWith("DeepSeek.Harness.Desktop", StringComparison.Ordinal) &&
                 !n.Equals("DeepSeek.Harness.Desktop.Core", StringComparison.Ordinal)))
            .ToArray();
        Assert.True(bad.Length == 0,
            $"R2 violate: Infrastructure assembly references presentation assemblies: {string.Join(", ", bad)}");
    }
}
