using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Warns on a step registered on the SetState message. Dataverse accepts the registration but never runs it: a
    /// <c>SetStateRequest</c> and an Update of <c>statecode</c> both fire SetStateDynamicEntity instead (verified live),
    /// so the step's logic silently never executes. The builder already registers <c>On&lt;SetStateRequest&gt;()</c>
    /// and a <c>[CustomMessage("SetState")]</c> marker on SetStateDynamicEntity (see SdkMessageNames); the one way left
    /// to land on SetState is a request or marker type that pins it with <c>[SdkMessage("SetState")]</c>, which is
    /// what this reports.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SetStateStepAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.SetStateNeverFires;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "A step on SetState never fires",
            messageFormat: "A step on SetState never fires: Dataverse runs a state change as SetStateDynamicEntity. Drop [SdkMessage(\"SetState\")] from '{0}' (On<SetStateRequest>() registers on SetStateDynamicEntity), or register on OnUpdate().FilterOn(statecode).",
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
                || member.Name is not GenericNameSyntax generic
                || generic.Identifier.ValueText is not ("On" or "OnAsyncOnly" or "OnMessage")
                || generic.TypeArgumentList.Arguments.Count != 1
                || context.SemanticModel.GetTypeInfo(generic.TypeArgumentList.Arguments[0], context.CancellationToken).Type is not INamedTypeSymbol type
                || !PinsSetState(type))
            {
                return;
            }

            if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), type.Name));
        }

        private static bool PinsSetState(INamedTypeSymbol type)
        {
            foreach (AttributeData attribute in type.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == "Vela.Messages.SdkMessageAttribute"
                    && attribute.ConstructorArguments.Length == 1
                    && attribute.ConstructorArguments[0].Value is string message
                    && string.Equals(message.Trim(), "SetState", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
