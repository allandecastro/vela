using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a handler bound with <c>.Handle(...)</c> on more than one image-capturing step in the same plugin. The
    /// generated typed step context is named after the handler (<c>{Handler}Context</c>), so two such steps sharing a
    /// handler would need two different contexts under the same name. Give each image-capturing step its own handler
    /// (or drop to <c>ctx.PostImageAs&lt;T&gt;()</c> with the plain context if you must share one).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SingleStepPerTypedContextHandlerAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.SingleStepPerTypedContextHandler;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Typed-context handler must bind a single step",
            messageFormat: "Handler '{0}' is bound on more than one image-capturing step; its generated typed context needs one step per handler",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "The generated typed step context is named after the handler, so a handler reused across image-capturing steps would collide. Use a distinct handler per image-capturing step.");

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

                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeClass(nodeContext, fluent), SyntaxKind.ClassDeclaration);
            });
        }

        private static void AnalyzeClass(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent)
        {
            ClassDeclarationSyntax declaration = (ClassDeclarationSyntax)context.Node;

            // Collect the handler-name occurrences of image-capturing steps in this class.
            Dictionary<string, List<ExpressionSyntax>> byHandler = new Dictionary<string, List<ExpressionSyntax>>(System.StringComparer.Ordinal);
            foreach (InvocationExpressionSyntax invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // Only this class's own steps: the generated context nests in the class that registers the step, so a
                // nested plugin's handlers live in a different scope and never collide with this one's (a container
                // class holding two plugins that both use OnUpdate is fine).
                if (!FluentStep.IsHandleCall(invocation, out MemberAccessExpressionSyntax member, out ExpressionSyntax argument) ||
                    invocation.FirstAncestorOrSelf<ClassDeclarationSyntax>() != declaration)
                {
                    continue;
                }

                string? handlerName = FluentStep.MethodGroupName(argument); // a lambda -> no generated context

                if (handlerName is null || !ChainCapturesTypedImage(member.Expression) ||
                    !FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
                {
                    continue;
                }

                if (!byHandler.TryGetValue(handlerName, out List<ExpressionSyntax>? occurrences))
                {
                    occurrences = new List<ExpressionSyntax>();
                    byHandler[handlerName] = occurrences;
                }

                occurrences.Add(argument);
            }

            foreach (KeyValuePair<string, List<ExpressionSyntax>> entry in byHandler)
            {
                if (entry.Value.Count < 2)
                {
                    continue;
                }

                foreach (ExpressionSyntax occurrence in entry.Value)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, occurrence.GetLocation(), entry.Key));
                }
            }
        }

        // True when the fluent chain preceding .Handle(...) captures a typed image (a WithPreImage/WithPostImage with
        // at least one column-selector lambda), i.e. a generated typed context would be emitted for this step.
        private static bool ChainCapturesTypedImage(ExpressionSyntax? receiver)
        {
            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                if ((call.MethodName == "WithPreImage" || call.MethodName == "WithPostImage") &&
                    call.Arguments.Any(a => a.Expression is LambdaExpressionSyntax))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
