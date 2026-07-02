using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace MUnique.OpenMU.SpecAnalyzer;

/// <summary>
/// Enforces CODING_RULES.md §3: No hard-coded values.
/// Flags magic numeric literals that aren't 0, 1, -1, or in trivially obvious positions.
/// Rule ID: OAPS003.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class MagicNumberAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "OAPS003",
        title: "Magic Number Literal",
        messageFormat: "Magic number '{0}' should be a named constant (CODING_RULES.md §3)",
        category: "SpecCompliance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // Trivial values that don't need naming
    private static readonly HashSet<int> TrivialInts = new() { 0, 1, -1 };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Only check literal expressions in method bodies
        context.RegisterSyntaxNodeAction(AnalyzeLiteral, SyntaxKind.NumericLiteralExpression);
    }

    private static void AnalyzeLiteral(SyntaxNodeAnalysisContext ctx)
    {
        var literal = (LiteralExpressionSyntax)ctx.Node;

        // Only flag if inside a method/constructor/property body
        if (!IsInExecutableContext(ctx.Node))
            return;

        // Only handle integer literals for now
        if (literal.Token.Value is int intVal && TrivialInts.Contains(intVal))
            return;

        // Skip values used in array initializers (indexers, sizes)
        if (IsArrayBoundary(literal))
            return;

        // Skip values used in switch case labels
        if (literal.Parent is CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax)
            return;

        // Skip attribute arguments
        if (literal.Parent is AttributeArgumentSyntax)
            return;

        // Report if it's a non-trivial integer or floating-point literal
        if (literal.Token.Value is int or long or byte or short or float or double or decimal)
        {
            var diagnostic = Diagnostic.Create(Rule, literal.GetLocation(), literal.Token.Text);
            ctx.ReportDiagnostic(diagnostic);
        }
    }

    private static bool IsInExecutableContext(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is BaseMethodDeclarationSyntax
                or PropertyDeclarationSyntax
                or AccessorDeclarationSyntax
                or FieldDeclarationSyntax
                or VariableDeclaratorSyntax)
                return true;

            if (ancestor is ClassDeclarationSyntax or StructDeclarationSyntax)
                return false;
        }
        return false;
    }

    private static bool IsArrayBoundary(LiteralExpressionSyntax literal)
    {
        // Skip if inside array rank (new int[5]) or array initializer
        return literal.Parent is ArrayRankSpecifierSyntax
            || literal.Parent is InitializerExpressionSyntax initExpr
               && initExpr.IsKind(SyntaxKind.ArrayInitializerExpression);
    }
}
