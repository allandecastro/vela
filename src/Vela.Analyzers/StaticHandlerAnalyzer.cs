using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a step handler passed to <c>.Handle(...)</c> as a <c>static</c> method. The handler must be an instance
    /// method (or a lambda) so it runs against the plugin instance and can reach its state - injected services, the
    /// step's unsecure/secure config, and other instance members. A static method group cannot, and does not fit the
    /// framework's instance-bound handler dispatch, so it is reported as an error at build instead of misbehaving at
    /// runtime. Lambdas and instance method groups are left alone.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class StaticHandlerAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA004";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Step handler must be an instance method",
            messageFormat: "Handler '{0}' is a static method; use an instance method (or a lambda) so it can access the plugin instance",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A step handler runs against the plugin instance. A static method cannot reach instance state (injected services, step config, other members) and does not fit the instance-bound handler dispatch. Use an instance method or a lambda.");

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

            // Cheap syntactic pre-filter: the terminal handler binder is named Handle.
            if (member.Name.Identifier.ValueText != "Handle")
            {
                return;
            }

            ArgumentSyntax? argument = invocation.ArgumentList.Arguments.FirstOrDefault();
            if (argument is null)
            {
                return;
            }

            ExpressionSyntax handler = argument.Expression;

            // A lambda / anonymous method is always fine - it captures the instance if it needs to.
            if (handler is SimpleLambdaExpressionSyntax || handler is ParenthesizedLambdaExpressionSyntax || handler is AnonymousMethodExpressionSyntax)
            {
                return;
            }

            // Only the fluent builder's own Handle counts; a same-named method on an unrelated type is ignored.
            if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
            {
                return;
            }

            // Flag only when we are confident the argument is a static method group: either the resolved symbol is a
            // static method, or (overloaded group) every candidate is static. Anything else is left alone.
            SymbolInfo info = context.SemanticModel.GetSymbolInfo(handler, context.CancellationToken);
            if (!IsStaticMethodGroup(info, out string methodName))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, handler.GetLocation(), methodName));
        }

        private static bool IsStaticMethodGroup(SymbolInfo info, out string methodName)
        {
            methodName = string.Empty;

            if (info.Symbol is IMethodSymbol method)
            {
                methodName = method.Name;
                return method.IsStatic;
            }

            // Ambiguous method group: only flag when every candidate is a static method, so an instance overload
            // never yields a false positive.
            if (info.Symbol is null && info.CandidateSymbols.Length > 0 && info.CandidateSymbols.All(s => s is IMethodSymbol m && m.IsStatic))
            {
                methodName = info.CandidateSymbols[0].Name;
                return true;
            }

            return false;
        }
    }
}
