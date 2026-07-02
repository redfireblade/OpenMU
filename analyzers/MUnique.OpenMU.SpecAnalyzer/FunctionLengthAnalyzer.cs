using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace MUnique.OpenMU.SpecAnalyzer;

/// <summary>
/// Enforces CODING_RULES.md §1: Keep functions short and focused.
/// Flags methods exceeding ~40 lines of body text.
/// Rule ID: OAPS004.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class FunctionLengthAnalyzer : DiagnosticAnalyzer
{
    private const int MaxLines = 40;

    private static readonly DiagnosticDescriptor Rule = new(
        id: "OAPS004",
        title: "Function Exceeds Recommended Length",
        messageFormat: "Method '{0}' is {1} lines long (max recommended: {2}) — consider extracting sub-functions (CODING_RULES.md §1)",
        category: "SpecCompliance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeConstructor, SyntaxKind.ConstructorDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx)
    {
        var method = (MethodDeclarationSyntax)ctx.Node;
        if (method.Body == null)
            return;

        var lineCount = method.Body.GetText().Lines.Count;
        if (lineCount > MaxLines)
        {
            var diagnostic = Diagnostic.Create(
                Rule,
                method.Identifier.GetLocation(),
                method.Identifier.Text,
                lineCount,
                MaxLines);
            ctx.ReportDiagnostic(diagnostic);
        }
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext ctx)
    {
        var ctor = (ConstructorDeclarationSyntax)ctx.Node;
        if (ctor.Body == null)
            return;

        var lineCount = ctor.Body.GetText().Lines.Count;
        if (lineCount > MaxLines)
        {
            var diagnostic = Diagnostic.Create(
                Rule,
                ctor.Identifier.GetLocation(),
                ctor.Identifier.Text,
                lineCount,
                MaxLines);
            ctx.ReportDiagnostic(diagnostic);
        }
    }
}
