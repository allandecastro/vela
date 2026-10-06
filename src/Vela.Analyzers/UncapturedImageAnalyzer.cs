using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags <c>ctx.PreImage</c> / <c>ctx.PostImage</c> in a handler whose step did not capture that image. The
    /// accessor is inherited from the base context (a post-operation context exposes both; a pre context exposes
    /// PreImage), so it compiles and returns <c>null</c> at runtime - a silent footgun. To keep false positives
    /// at zero this fires only in the unambiguous case: the handler is an instance method referenced by exactly
    /// one <c>RegisterStep()</c> chain in the same class, and that chain has no matching
    /// <c>WithPreImage(...)</c> / <c>WithPostImage(...)</c>. Lambda handlers and handlers shared across steps are
    /// left alone.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class UncapturedImageAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA017";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Image read but not captured by the step",
            messageFormat: "The step bound to this handler captures no {0}; 'ctx.{1}' is inherited from the base context and returns null at runtime. Add .{2}(...) to the step (or read the image it captures).",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "ctx.PreImage / ctx.PostImage are populated only when the step captures them with WithPreImage(...) / WithPostImage(...). Reading an uncaptured image compiles (the base context exposes the property) but returns null.");

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

                var perTypeBindings = new ConcurrentDictionary<TypeDeclarationSyntax, IReadOnlyDictionary<ISymbol, (int Chains, bool HasPreImage, bool HasPostImage)>>();
                // SimpleMemberAccess covers ctx.PreImage; MemberBinding covers the null-conditional ctx?.PreImage
                // (the name sits in a member-binding node whose receiver is the enclosing conditional access).
                start.RegisterSyntaxNodeAction(
                    nodeContext => AnalyzeAccess(nodeContext, contextType, fluent, perTypeBindings),
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxKind.MemberBindingExpression);
            });
        }

        private static void AnalyzeAccess(SyntaxNodeAnalysisContext context, INamedTypeSymbol contextType, INamedTypeSymbol fluent, ConcurrentDictionary<TypeDeclarationSyntax, IReadOnlyDictionary<ISymbol, (int Chains, bool HasPreImage, bool HasPostImage)>> perTypeBindings)
        {
            SimpleNameSyntax nameSyntax;
            ExpressionSyntax receiver;
            switch (context.Node)
            {
                case MemberAccessExpressionSyntax ma:
                    nameSyntax = ma.Name;
                    receiver = ma.Expression;
                    break;
                case MemberBindingExpressionSyntax mb when mb.Parent?.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is ConditionalAccessExpressionSyntax conditional:
                    nameSyntax = mb.Name;
                    receiver = conditional.Expression;
                    break;
                default:
                    return;
            }

            string name = nameSyntax.Identifier.ValueText;
            bool isPre = name == "PreImage";
            bool isPost = name == "PostImage";
            if (!isPre && !isPost)
            {
                return;
            }

            // Only a property access on a Vela context (not ctx.PreImageAs<T>(), not an unrelated PreImage).
            if (context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol is not IPropertySymbol)
            {
                return;
            }

            ITypeSymbol? receiverType = context.SemanticModel.GetTypeInfo(receiver, context.CancellationToken).Type;
            if (receiverType is null || !ImplementsContext(receiverType, contextType))
            {
                return;
            }

            MethodDeclarationSyntax? handler = context.Node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            TypeDeclarationSyntax? owningType = handler?.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (handler is null || owningType is null)
            {
                return;
            }

            if (context.SemanticModel.GetDeclaredSymbol(handler, context.CancellationToken) is not IMethodSymbol handlerSymbol)
            {
                return;
            }

            IReadOnlyDictionary<ISymbol, (int Chains, bool HasPreImage, bool HasPostImage)> bindings = perTypeBindings.GetOrAdd(
                owningType, ot => BuildHandlerBindings(ot, context.SemanticModel, fluent, context.CancellationToken));

            if (!bindings.TryGetValue(handlerSymbol, out (int Chains, bool HasPreImage, bool HasPostImage) binding) || binding.Chains != 1)
            {
                return;
            }

            if (isPre && !binding.HasPreImage)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, nameSyntax.GetLocation(), "pre-image", "PreImage", "WithPreImage"));
            }
            else if (isPost && !binding.HasPostImage)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, nameSyntax.GetLocation(), "post-image", "PostImage", "WithPostImage"));
            }
        }

        // Scans a type once, mapping each method-group handler bound by a RegisterStep()...Handle(handler) chain to
        // the number of chains that own it and whether any of those chains declares WithPreImage / WithPostImage.
        private static IReadOnlyDictionary<ISymbol, (int Chains, bool HasPreImage, bool HasPostImage)> BuildHandlerBindings(
            TypeDeclarationSyntax owningType, SemanticModel model, INamedTypeSymbol fluent, CancellationToken cancellationToken)
        {
            var result = new Dictionary<ISymbol, (int Chains, bool HasPreImage, bool HasPostImage)>(SymbolEqualityComparer.Default);
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

                if (FluentStep.BoundHandler(model, handlerArg.Expression, cancellationToken) is not IMethodSymbol bound
                    || !FluentStep.IsFluentMethod(model, handleCall, fluent, cancellationToken))
                {
                    continue;
                }

                bool hasPre = SpineHasCall(handleMember.Expression, "WithPreImage");
                bool hasPost = SpineHasCall(handleMember.Expression, "WithPostImage");
                (int Chains, bool HasPreImage, bool HasPostImage) existing = result.TryGetValue(bound, out (int Chains, bool HasPreImage, bool HasPostImage) e) ? e : (0, false, false);
                result[bound] = (existing.Chains + 1, existing.HasPreImage || hasPre, existing.HasPostImage || hasPost);
            }

            return result;
        }

        private static bool ImplementsContext(ITypeSymbol type, INamedTypeSymbol contextType)
            => SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, contextType)
                || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, contextType));

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
    }
}
