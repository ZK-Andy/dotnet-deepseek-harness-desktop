using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeepSeek.Harness.Desktop.Analyzers.Tests;

/// <summary>DSH0001（D003 机器化）的行为契约：只红无类型空 <c>catch</c>，其余形态一律放行。</summary>
public class EmptyCatchAnalyzerTests
{
    private static async Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string code)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
        string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        IEnumerable<MetadataReference> references = trusted.Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(
            "DshAnalyzerProbe",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analyzer = new EmptyCatchAnalyzer();
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer))
            .GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>无类型真空体即红（<c>CliShimTests</c> 存量形态），且为 error 级。</summary>
    [Fact]
    public async Task TypelessEmptyCatch_ReportsDSH0001AsError()
    {
        const string code = """
            using System;
            class C
            {
                void M() { try { Foo(); } catch { } }
                void Foo() { }
            }
            """;
        IReadOnlyList<Diagnostic> diagnostics = await AnalyzeAsync(code);
        Diagnostic hit = Assert.Single(diagnostics);
        Assert.Equal(EmptyCatchAnalyzer.DiagnosticId, hit.Id);
        Assert.Equal(DiagnosticSeverity.Error, hit.Severity);
    }

    /// <summary>无类型注释空体即红（注释不计语句，<c>MarketInstallHelper</c> 存量形态）。</summary>
    [Fact]
    public async Task TypelessCommentOnlyCatch_ReportsDSH0001()
    {
        const string code = """
            using System;
            class C
            {
                void M() { try { Foo(); } catch { /* 清理失败不影响结果 */ } }
                void Foo() { }
            }
            """;
        IReadOnlyList<Diagnostic> diagnostics = await AnalyzeAsync(code);
        Assert.Equal(EmptyCatchAnalyzer.DiagnosticId, Assert.Single(diagnostics).Id);
    }

    /// <summary>无类型但有语句即放行（非空不受 D003 约束）。</summary>
    [Fact]
    public async Task TypelessNonEmptyCatch_NoDiagnostic()
    {
        const string code = """
            using System;
            class C
            {
                void M() { try { Foo(); } catch { Foo(); } }
                void Foo() { }
            }
            """;
        Assert.Empty(await AnalyzeAsync(code));
    }

    /// <summary>具名类型真空体即放行（命名所吞即合规，无变量亦可）。</summary>
    [Fact]
    public async Task TypedEmptyCatch_NoDiagnostic()
    {
        const string code = """
            using System;
            class C
            {
                void M() { try { Foo(); } catch (Exception) { } }
                void Foo() { }
            }
            """;
        Assert.Empty(await AnalyzeAsync(code));
    }

    /// <summary>具名类型注释空体即放行（D003 示例合规形）。</summary>
    [Fact]
    public async Task TypedCommentOnlyCatch_NoDiagnostic()
    {
        const string code = """
            using System;
            class C
            {
                void M() { try { Foo(); } catch (InvalidOperationException) { /* 进程已退出 */ } }
                void Foo() { }
            }
            """;
        Assert.Empty(await AnalyzeAsync(code));
    }

    /// <summary>无类型 filter 空体即红（`catch when (…)` 无声明亦须命名类型，R2-S1 收口）。</summary>
    [Fact]
    public async Task TypelessFilterEmptyCatch_ReportsDSH0001()
    {
        const string code = """
            using System;
            class C
            {
                void M() { try { Foo(); } catch when (DateTime.Now.Ticks > 0) { } }
                void Foo() { }
            }
            """;
        IReadOnlyList<Diagnostic> diagnostics = await AnalyzeAsync(code);
        Assert.Equal(EmptyCatchAnalyzer.DiagnosticId, Assert.Single(diagnostics).Id);
    }
}
