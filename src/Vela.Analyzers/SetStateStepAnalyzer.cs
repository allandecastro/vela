using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Warns on a step registered on the SetState message (<c>OnMessage("SetState")</c> or
    /// <c>OnMessage(PluginMessages.SetState)</c>). Dataverse accepts the registration but never runs it: a
    /// <c>SetStateRequest</c> and an Update of <c>statecode</c> both fire SetStateDynamicEntity instead (verified live),
    /// so the step's logic silently never executes.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SetStateStepAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.SetStateNeverFires;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "A step on SetState never fires",
            messageFormat: "A step on SetState never fires: Dataverse runs a state change as SetStateDynamicEntity. Register on OnUpdate().FilterOn(statecode) or on OnMessage(PluginMessages.SetStateDynamicEntity).",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Dataverse registers a SetState step but never executes it: a SetStateRequest and an Update of statecode fire the SetStateDynamicEntity steps (and the Update steps), not the SetState ones.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol? fluent = start.Compilation.GetTypeByMetadataName("Vela.Steps.IFluentStep");
                if (fluent is null)
                {
                    return;
                }

                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeInvocation(nodeContext, fluent), SyntaxKind.InvocationExpression);
            });
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax member
                || member.Name.Identifier.ValueText != "OnMessage"
                || invocation.ArgumentList.Arguments.Count != 1)
            {
                return;
            }

            // A literal or a constant (PluginMessages.SetState) both carry a compile-time value.
            Optional<object?> value = context.SemanticModel.GetConstantValue(invocation.ArgumentList.Arguments[0].Expression, context.CancellationToken);
            if (!value.HasValue || value.Value is not string message
                || !string.Equals(message.Trim(), "SetState", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation()));
        }
    }
}
