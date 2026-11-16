using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a step registration chain that sets the same single-value modifier twice (for example two
    /// <c>.WithExecutionOrder(...)</c>, or two <c>.FilterOn(...)</c>). Each of these overwrites the earlier value
    /// rather than adding to it, so a second call silently drops what the first one set. The runtime builder also
    /// throws on the second call; this analyzer surfaces the same mistake in the editor and at build (an error),
    /// so it never compiles in the common inline-chain case. Chains split across locals are left to the runtime
    /// guard (matching VELA002's philosophy of not flagging split chains that may complete later).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DuplicateModifierAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.DuplicateModifier;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Duplicate step modifier",
            messageFormat: "'{0}' is already set on this step; call it at most once",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A single-value step modifier (FilterOn, ForRelationship, SkipWhenDepthAbove or a With... setter) overwrites the earlier value when called twice, silently dropping it. Call each at most once per step.");

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

            string name = member.Name.Identifier.ValueText;
            // The single-value modifiers, the list the runtime builder enforces (shared as source, so the two cannot
            // drift). Disabled() and the images are not in it: an idempotent toggle, and distinct captures.
            if (!Vela.Steps.SetOnceModifiers.Contains(name))
            {
                return;
            }

            // Only the fluent builder's own methods count; a same-named method on an unrelated type is ignored.
            if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
            {
                return;
            }

            // Walk the receiver spine (not into argument lambdas): a prior call to the same modifier means this
            // one is the duplicate. Reporting on the outer (current) call flags the second and any later call.
            if (HasEarlierCall(member.Expression, name, context.SemanticModel, fluent, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), name));
            }
        }

        private static bool HasEarlierCall(ExpressionSyntax? receiver, string name, SemanticModel model, INamedTypeSymbol fluent, System.Threading.CancellationToken cancellationToken)
        {
            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                if (call.MethodName == name && FluentStep.IsFluentMethod(model, call.Invocation, fluent, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
