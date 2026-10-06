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
    /// Flags writing to the Target (<c>ctx.Request.Target</c> / <c>ctx.GetTarget&lt;T&gt;()</c>) inside a handler
    /// bound to an <b>asynchronous</b> step. An async step runs after the main operation has committed, in its own
    /// execution, so mutating the Target there has no effect: it is never written back to the database and no later
    /// step observes it. To keep false positives at zero this fires only in the unambiguous case: the handler is an
    /// instance method bound by exactly one <c>RegisterStep()</c> chain, and that chain is <c>.Async()</c>. Writes
    /// through a local copy of the Target are not tracked, and a handler shared across steps is left alone.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class AsyncTargetWriteAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA018";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Write to the Target in an asynchronous step",
            messageFormat: "This async step's handler writes to the Target, which has no effect: an async step runs after the operation commits, so the change is neither persisted nor seen by a later step. Call ctx.Service.Update(...) with the change, or move the logic to a synchronous step.",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "An asynchronous step executes after the main operation has committed. Mutating ctx.Request.Target or ctx.GetTarget<T>() is lost: it is not written back to the database and no downstream step observes it.");

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

                var perTypeBindings = new ConcurrentDictionary<TypeDeclarationSyntax, IReadOnlyDictionary<ISymbol, (int Chains, bool IsAsync)>>();
                start.RegisterSyntaxNodeAction(
                    nodeContext => AnalyzeWrite(nodeContext, fluent, perTypeBindings),
                    // Every form that mutates its operand: plain and compound assignment (+=, ??=, ...) and
                    // increment/decrement (++ / --). All write to the Target the same way a simple assignment does.
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxKind.AddAssignmentExpression,
                    SyntaxKind.SubtractAssignmentExpression,
                    SyntaxKind.MultiplyAssignmentExpression,
                    SyntaxKind.DivideAssignmentExpression,
                    SyntaxKind.ModuloAssignmentExpression,
                    SyntaxKind.AndAssignmentExpression,
                    SyntaxKind.OrAssignmentExpression,
                    SyntaxKind.ExclusiveOrAssignmentExpression,
                    SyntaxKind.LeftShiftAssignmentExpression,
                    SyntaxKind.RightShiftAssignmentExpression,
                    SyntaxKind.CoalesceAssignmentExpression,
                    SyntaxKind.PreIncrementExpression,
                    SyntaxKind.PostIncrementExpression,
                    SyntaxKind.PreDecrementExpression,
                    SyntaxKind.PostDecrementExpression);
            });
        }

        private static void AnalyzeWrite(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent, ConcurrentDictionary<TypeDeclarationSyntax, IReadOnlyDictionary<ISymbol, (int Chains, bool IsAsync)>> perTypeBindings)
        {
            // The written expression: the left of an assignment, or the operand of ++/--.
            ExpressionSyntax? written = context.Node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                PrefixUnaryExpressionSyntax prefix => prefix.Operand,
                PostfixUnaryExpressionSyntax postfix => postfix.Operand,
                _ => null,
            };

            // It must be a write INTO the Target entity (an attribute/property or indexer on it), not a reassignment
            // of the Target reference itself.
            ExpressionSyntax? writeBase = written switch
            {
                ElementAccessExpressionSyntax elementAccess => elementAccess.Expression,
                MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
                _ => null,
            };

            if (written is null || writeBase is null || !IsTargetExpression(writeBase, context.SemanticModel, context.CancellationToken))
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

            IReadOnlyDictionary<ISymbol, (int Chains, bool IsAsync)> bindings = perTypeBindings.GetOrAdd(
                owningType, ot => BuildHandlerBindings(ot, context.SemanticModel, fluent, context.CancellationToken));

            if (bindings.TryGetValue(handlerSymbol, out (int Chains, bool IsAsync) binding) && binding.Chains == 1 && binding.IsAsync)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, written.GetLocation()));
            }
        }

        // True when the expression is the step Target: ctx.GetTarget<T>() (an extension/method named GetTarget) or a
        // request's Target property - both in a Vela namespace, so unrelated .Target members are not matched.
        private static bool IsTargetExpression(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
        {
            ExpressionSyntax current = expression;
            while (current is ParenthesizedExpressionSyntax paren)
            {
                current = paren.Expression;
            }

            return model.GetSymbolInfo(current, cancellationToken).Symbol switch
            {
                IMethodSymbol method => method.Name == "GetTarget" && InVela(method.ContainingNamespace),
                IPropertySymbol property => property.Name == "Target" && InVela(property.ContainingType?.ContainingNamespace),
                _ => false,
            };
        }

        private static bool InVela(INamespaceSymbol? ns)
            => ns is not null && ns.ToDisplayString().StartsWith("Vela", System.StringComparison.Ordinal);

        // Scans a type once, mapping each method-group handler bound by a RegisterStep()...Handle(handler) chain to
        // the number of chains that own it and whether any of those chains is .Async().
        private static IReadOnlyDictionary<ISymbol, (int Chains, bool IsAsync)> BuildHandlerBindings(
            TypeDeclarationSyntax owningType, SemanticModel model, INamedTypeSymbol fluent, CancellationToken cancellationToken)
        {
            var result = new Dictionary<ISymbol, (int Chains, bool IsAsync)>(SymbolEqualityComparer.Default);
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

                if (model.GetSymbolInfo(handlerArg.Expression, cancellationToken).Symbol is not IMethodSymbol bound
                    || !FluentStep.IsFluentMethod(model, handleCall, fluent, cancellationToken))
                {
                    continue;
                }

                bool isAsync = SpineHasCall(handleMember.Expression, "Async");
                (int Chains, bool IsAsync) existing = result.TryGetValue(bound, out (int Chains, bool IsAsync) e) ? e : (0, false);
                result[bound] = (existing.Chains + 1, existing.IsAsync || isAsync);
            }

            return result;
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
    }
}
