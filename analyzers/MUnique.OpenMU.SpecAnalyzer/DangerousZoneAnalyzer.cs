using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace MUnique.OpenMU.SpecAnalyzer;

/// <summary>
/// Enforces WIKI/10-dangerous-zones.md: detects references from AIPlayer code
/// into S/A-level forbidden namespaces (Network, Startup, RemoteView, etc.).
/// Rule IDs: OAPS001–OAPS006.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class DangerousZoneAnalyzer : DiagnosticAnalyzer
{
    // S-Level (error) — Network encryption, startup, remote view serialization
    private static readonly DiagnosticDescriptor SLevelRule = new(
        id: "OAPS001",
        title: "S-Level Dangerous Zone Reference",
        messageFormat: "S-Level dangerous zone: '{0}' must not be referenced by AI code (WIKI/10 §10.2)",
        category: "SpecCompliance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // A-Level (warning) — Other server types (Connect, Chat, Friend, Guild)
    private static readonly DiagnosticDescriptor ALevelRule = new(
        id: "OAPS002",
        title: "A-Level Dangerous Zone Reference",
        messageFormat: "A-Level dangerous zone: '{0}' should not be referenced by AI code (WIKI/10 §10.3)",
        category: "SpecCompliance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly string[] SLevelNamespaces =
    {
        "MUnique.OpenMU.Network",
        "MUnique.OpenMU.Startup",
        "MUnique.OpenMU.GameServer.RemoteView",
    };

    private static readonly string[] ALevelNamespaces =
    {
        "MUnique.OpenMU.ConnectServer",
        "MUnique.OpenMU.ChatServer",
        "MUnique.OpenMU.FriendServer",
        "MUnique.OpenMU.GuildServer",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(SLevelRule, ALevelRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Check using directives
        context.RegisterSyntaxNodeAction(AnalyzeUsingDirective, SyntaxKind.UsingDirective);

        // Check fully-qualified type references (e.g., Network.SomeType)
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext ctx)
    {
        var usingDirective = (UsingDirectiveSyntax)ctx.Node;
        var name = usingDirective.Name?.ToString();
        if (name == null)
            return;

        // Skip alias directives (using X = Y;) — those reference types, not namespaces
        if (usingDirective.Alias != null)
            return;

        CheckNamespace(ctx, name, usingDirective.GetLocation());
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext ctx)
    {
        // Detect fully-qualified names like Network.SomeClass.Method()
        var memberAccess = (MemberAccessExpressionSyntax)ctx.Node;

        // Only check top-level expressions (e.g., Network.SomeType, not foo.Network)
        if (memberAccess.Expression is not IdentifierNameSyntax identifier)
            return;

        var fullName = identifier.Identifier.Text + "." + memberAccess.Name.Identifier.Text;

        // Only flag if it exactly matches a dangerous namespace prefix as the root
        CheckNamespace(ctx, fullName, memberAccess.GetLocation());
    }

    private static void CheckNamespace(SyntaxNodeAnalysisContext ctx, string name, Location location)
    {
        foreach (var ns in SLevelNamespaces)
        {
            if (name.StartsWith(ns, System.StringComparison.Ordinal))
            {
                var diagnostic = Diagnostic.Create(SLevelRule, location, name);
                ctx.ReportDiagnostic(diagnostic);
                return;
            }
        }

        foreach (var ns in ALevelNamespaces)
        {
            if (name.StartsWith(ns, System.StringComparison.Ordinal))
            {
                var diagnostic = Diagnostic.Create(ALevelRule, location, name);
                ctx.ReportDiagnostic(diagnostic);
                return;
            }
        }
    }
}
