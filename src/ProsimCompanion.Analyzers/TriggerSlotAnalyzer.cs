using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ProsimCompanion.Analyzers;

/// <summary>
/// PCGSX001: only <c>GsxTriggerSlot</c> may send <c>service.trigger</c> (the trigger-slot
/// invariant, campaign #77 / CONTEXT.md "trigger slot"). GSX silently drops rapid-fire
/// triggers, so every sender must go through the single serialized slot — a direct
/// <c>SendCommandAsync("service.trigger", …)</c> anywhere else is a compile error.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TriggerSlotAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "PCGSX001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "service.trigger must go through GsxTriggerSlot",
        messageFormat: "Do not send 'service.trigger' directly — dispatch through IGsxTriggerSlot (the trigger slot is the only permitted sender)",
        category: "Design",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "GSX silently drops rapid-fire service triggers; exactly one trigger may be "
            + "in flight at a time. GsxTriggerSlot owns that invariant, so every sender must go "
            + "through it (see CONTEXT.md 'trigger slot').");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (GetInvokedMethodName(invocation) != "SendCommandAsync")
        {
            return;
        }

        var firstArgument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (firstArgument is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression)
            || literal.Token.ValueText != "service.trigger")
        {
            return;
        }

        // The slot itself is the one permitted sender.
        var containingType = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType?.Identifier.ValueText == "GsxTriggerSlot")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };
}
