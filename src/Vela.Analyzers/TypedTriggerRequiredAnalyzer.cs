using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// One way per message: <c>On&lt;TRequest&gt;()</c> / <c>OnAsyncOnly&lt;TRequest&gt;()</c> serve the messages that have
    /// no typed trigger. A request whose message has one (<c>On&lt;UpdateRequest&gt;()</c> when <c>OnUpdate()</c> exists)
    /// is an error; the runtime rejects the same registration. "Has a typed trigger" is read off the trigger interface
    /// itself (an <c>On{Message}()</c> member), so the generator's message model stays the only list.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class TypedTriggerRequiredAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.TypedTriggerRequired;

        /// <summary>The diagnostic property carrying the typed trigger to use (for the code fix).</summary>
        public const string TriggerProperty = "Trigger";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "A message with a typed trigger is registered through it",
            messageFormat: "{0} has a typed trigger: register the step with {1}() instead of {2}<{3}>(); {2}<TRequest>() is only for messages without one",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Each message is registered one way. A message with a typed trigger (OnCreate, OnUpdate, OnAssign, OnSendEmail, ...) is registered through it; On<TRequest>() and OnAsyncOnly<TRequest>() are for messages without one (custom APIs and the messages Vela does not model). The runtime rejects the same registration.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol? fluent = start.Compilation.GetTypeByMetadataName(FluentStep.FluentStepMetadataName);
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
            if (invocation.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax name } ||
                (name.Identifier.ValueText != "On" && name.Identifier.ValueText != "OnAsyncOnly") ||
                name.TypeArgumentList.Arguments.Count != 1)
            {
                return;
            }

            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
                !FluentStep.ImplementsFluent(method.ContainingType, fluent) ||
                method.TypeArguments.Length != 1 ||
                FluentStep.SdkRequestMessage(method.TypeArguments[0]) is not { } message ||
                FluentStep.FindTypedTrigger(method.ContainingType, message) is not { } trigger)
            {
                return;
            }

            ImmutableDictionary<string, string?> properties = ImmutableDictionary<string, string?>.Empty.Add(TriggerProperty, trigger);
            context.ReportDiagnostic(Diagnostic.Create(Rule, name.GetLocation(), properties,
                message, trigger, name.Identifier.ValueText, method.TypeArguments[0].Name));
        }
    }
}
