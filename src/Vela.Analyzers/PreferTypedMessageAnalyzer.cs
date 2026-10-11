using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Suggests the typed step trigger over the permissive string form for a modelled message: prefer
    /// <c>OnCreate()</c> to <c>OnMessage("create")</c>. The typed method gives compile-time entity applicability and
    /// a typed handler context, so it is the better default. This is advisory (a <b>warning</b>, not an error):
    /// <c>OnMessage("...")</c> stays valid and is the right tool for a message Vela does not model.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PreferTypedMessageAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.PreferTypedMessage;

        /// <summary>The diagnostic property carrying the typed trigger to use (for the code fix).</summary>
        public const string TriggerProperty = "Trigger";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Prefer the typed step trigger over OnMessage(string)",
            messageFormat: "{1} has a typed trigger: register the step with {0}() instead of OnMessage(\"{1}\"); OnMessage(...) is only for messages without one",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Each message is registered one way. A message with a typed trigger (OnCreate, OnUpdate, ...) is registered through it, which also gives a typed handler context and compile-time entity applicability; OnMessage(\"...\") is for messages without one. A warning: the string form still deploys.");

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
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                return;
            }

            if (member.Name.Identifier.ValueText != "OnMessage")
            {
                return;
            }

            // Only a single string-literal argument is considered; a variable/const message is left alone.
            SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count != 1 || arguments[0].Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return;
            }

            string message = literal.Token.ValueText;
            if (message.Trim().Length == 0 ||
                context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
                !FluentStep.ImplementsFluent(method.ContainingType, fluent) ||
                FluentStep.FindTypedTrigger(method.ContainingType, message) is not { } suggestion)
            {
                return;
            }

            ImmutableDictionary<string, string?> properties = ImmutableDictionary<string, string?>.Empty.Add(TriggerProperty, suggestion);
            context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), properties, suggestion, message));
        }
    }
}
