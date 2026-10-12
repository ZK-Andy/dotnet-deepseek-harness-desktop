using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeepSeek.Harness.Desktop.Analyzers;

/// <summary>D003 的机器化：空 <c>catch</c> 体（零语句，注释不计）必须显式声明异常类型，无类型 <c>catch</c> 即红。</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyCatchAnalyzer : DiagnosticAnalyzer
{
    /// <summary>诊断 ID。</summary>
    public const string DiagnosticId = "DSH0001";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "空 catch 必须命名所吞异常",
        "空 catch 必须命名所吞异常类型（如 catch (InvalidOperationException)）",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "D003: comment-only bodies count as empty; catch blocks with statements are out of scope.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeCatch, SyntaxKind.CatchClause);
    }

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context)
    {
        var clause = (CatchClauseSyntax)context.Node;
        if (clause.Declaration is not null)
        {
            return;
        }

        if (clause.Block is null || clause.Block.Statements.Count != 0)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(s_rule, clause.CatchKeyword.GetLocation()));
    }
}
