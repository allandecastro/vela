using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags <c>ctx.HasChanged(...)</c> / <c>ctx.HasAnyChanged(...)</c> in a handler whose step registers no
    /// pre-image: these throw at runtime because they diff the Target against a registered pre-image
    /// (StepBuilder / HasChangedCore). To keep false positives at zero this fires only in the unambiguous case:
    /// the handler is an instance method referenced by exactly one <c>RegisterStep()</c> chain in the same class,
    /// that chain has no <c>WithPreImage(...)</c>, and the call uses the default pre-image alias. Lambda handlers,
    /// handlers shared across steps, custom aliases, and anything else are left alone.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HasChangedPreImageAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA009";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "HasChanged without a registered pre-image",
            messageFormat: "'{0}' requires a registered pre-image, but the step that runs this handler has no WithPreImage(...); it will throw at runtime",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "HasChanged / HasAnyChanged diff the Target against a registered pre-image and throw when none is registered. Add WithPreImage(...) to the step, or remove the change check.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol? contextType = start.Compilation.GetTypeByMetadataName("Vela.Context.ILocalPluginContext");
                INamedTypeSymbol? fluent = start.Compilation.GetTypeByMetadataName("Vela.Steps.IFluentStep");
                if (contextType is null || fluent is null)
                {
                    return;
                }

                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeInvocation(nodeContext, contextType, fluent), SyntaxKind.InvocationExpression);
            });
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, INamedTypeSymbol contextType, INamedTypeSymbol fluent)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                return;
            }

            string name = member.Name.Identifier.ValueText;
            if (name != "HasChanged" && name != "HasAnyChanged")
            {
                return;
            }

            // Confirm it is the Vela change-detection extension over ILocalPluginContext.
            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
                || !IsContextChangeCheck(method, contextType))
            {
                return;
            }

            // A HasChanged with an explicit (non-default) pre-image alias is out of scope - matching aliases to the
            // WithPreImage alias is ambiguous, so skip to avoid false positives. HasAnyChanged always uses default.
            if (name == "HasChanged" && invocation.ArgumentList.Arguments.Count > 1)
            {
                return;
            }

            // The enclosing handler must be a plain method (not a lambda/local function/property) so it can be bound
            // to a RegisterStep chain by name.
            MethodDeclarationSyntax? handler = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            TypeDeclarationSyntax? owningType = handler?.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (handler is null || owningType is null)
            {
                return;
            }

            if (context.SemanticModel.GetDeclaredSymbol(handler, context.CancellationToken) is not IMethodSymbol handlerSymbol)
            {
                return;
            }

            // Find every RegisterStep()...Handle(handler) chain in the same type that binds THIS handler by method
            // group. Only act when exactly one chain owns it (unambiguous) and that chain has no WithPreImage.
            int owningChains = 0;
            bool anyChainHasPreImage = false;
            foreach (InvocationExpressionSyntax handleCall in owningType.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (handleCall.Expression is not MemberAccessExpressionSyntax handleMember || handleMember.Name.Identifier.ValueText != "Handle")
                {
                    continue;
                }

                ArgumentSyntax? handlerArg = handleCall.ArgumentList.Arguments.FirstOrDefault();
                if (handlerArg is null)
                {
                    continue;
                }

                if (context.SemanticModel.GetSymbolInfo(handlerArg.Expression, context.CancellationToken).Symbol is not IMethodSymbol bound
                    || !SymbolEqualityComparer.Default.Equals(bound, handlerSymbol))
                {
                    continue;
                }

                if (!IsFluentMethod(context.SemanticModel, handleCall, fluent, context.CancellationToken))
                {
                    continue;
                }

                owningChains++;
                if (SpineHasCall(handleMember.Expression, "WithPreImage"))
                {
                    anyChainHasPreImage = true;
                }
            }

            if (owningChains == 1 && !anyChainHasPreImage)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), name));
            }
        }

        private static bool IsContextChangeCheck(IMethodSymbol method, INamedTypeSymbol contextType)
        {
            ITypeSymbol? receiver = method.ReducedFrom is not null
                ? method.ReceiverType
                : method.Parameters.Length > 0 ? method.Parameters[0].Type : null;

            if (receiver is null)
            {
                return false;
            }

            if (SymbolEqualityComparer.Default.Equals(receiver.OriginalDefinition, contextType))
            {
                return true;
            }

            return receiver.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, contextType));
        }

        private static bool SpineHasCall(ExpressionSyntax? receiver, string methodName)
        {
            ExpressionSyntax? current = receiver;
            while (current is not null)
            {
                switch (current)
                {
                    case InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax ma:
                        if (ma.Name.Identifier.ValueText == methodName)
                        {
                            return true;
                        }

                        current = ma.Expression;
                        break;
                    case MemberAccessExpressionSyntax ma2:
                        current = ma2.Expression;
                        break;
                    case ParenthesizedExpressionSyntax paren:
                        current = paren.Expression;
                        break;
                    default:
                        current = null;
                        break;
                }
            }

            return false;
        }

        private static bool IsFluentMethod(SemanticModel model, InvocationExpressionSyntax invocation, INamedTypeSymbol fluent, System.Threading.CancellationToken cancellationToken)
        {
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            {
                return false;
            }

            return ImplementsFluent(method.ContainingType, fluent);
        }

        private static bool ImplementsFluent(ITypeSymbol type, INamedTypeSymbol fluent)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, fluent))
            {
                return true;
            }

            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, fluent))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
