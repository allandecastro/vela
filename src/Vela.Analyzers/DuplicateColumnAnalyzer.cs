using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags the same column listed twice in a single <c>FilterOn(...)</c>, <c>WithPreImage(...)</c>,
    /// <c>WithPostImage(...)</c>, or typed-query <c>Select(...)</c> call (for example
    /// <c>FilterOn(a =&gt; a.Name, a =&gt; a.Name)</c> or <c>Query.For&lt;T&gt;().Select(c =&gt; c.Name, c =&gt; c.Name)</c>).
    /// A repeated column adds nothing and is almost always a copy/paste slip, so it is reported at build. Only lambda
    /// column selectors are considered; a leading image alias string is ignored. The relationship selector
    /// <c>Include(...)</c> is a different method (handled by VELA010) and is not considered here.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DuplicateColumnAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA005";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Duplicate column in a selector list",
            messageFormat: "Column '{0}' is listed more than once in this call; list each column at most once",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "The same column appears more than once in a single FilterOn / WithPreImage / WithPostImage / typed-query Select call. A repeated column has no effect and is usually a copy/paste mistake.");

        // Step-authoring column-list methods, gated on Vela.Steps.IFluentStep.
        private static readonly ImmutableHashSet<string> FluentColumnMethods = ImmutableHashSet.Create(
            "FilterOn",
            "WithPreImage",
            "WithPostImage");

        // The typed-query column selector Query.For<T>().Select(...); gated on the Vela.Querying namespace so an
        // unrelated Select (LINQ, a user type) is ignored. Include (relationship selector) is a different name.
        private const string SelectMethod = "Select";

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

            string methodName = member.Name.Identifier.ValueText;
            bool isSelect = methodName == SelectMethod;
            if (!isSelect && !FluentColumnMethods.Contains(methodName))
            {
                return;
            }

            // Scope to Vela's own methods: the typed-query Select is recognised by its declaring namespace, the
            // step-authoring methods by the IFluentStep marker, so a same-named method on an unrelated type is ignored.
            bool recognised = isSelect
                ? IsVelaQuerySelect(context.SemanticModel, invocation, context.CancellationToken)
                : FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken);
            if (!recognised)
            {
                return;
            }

            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                if (!TryGetSelectedColumn(argument.Expression, out string column))
                {
                    continue; // not a column selector (e.g. an image alias string) - ignore
                }

                if (!seen.Add(column))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, argument.GetLocation(), column));
                }
            }
        }

        // True when the invoked Select is Vela's typed-query column selector (TypedQuery<T>.Select /
        // TypedLink<T>.Select / the QueryExpression Select extension), identified by the Vela.Querying namespace of
        // the declaring type. Anything else named Select (LINQ, a user type) is not a duplicate-column concern here.
        private static bool IsVelaQuerySelect(SemanticModel model, InvocationExpressionSyntax invocation, System.Threading.CancellationToken cancellationToken)
        {
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            {
                return false;
            }

            INamespaceSymbol? ns = method.ContainingType?.ContainingNamespace;
            return ns is not null && ns.ToDisplayString() == "Vela.Querying";
        }

        // A column selector is a lambda whose body is a member access on the parameter, e.g. a => a.Name. Returns
        // the selected member name; false for anything that is not such a selector.
        private static bool TryGetSelectedColumn(ExpressionSyntax expression, out string column)
        {
            column = string.Empty;

            ExpressionSyntax? body = expression switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Body as ExpressionSyntax,
                ParenthesizedLambdaExpressionSyntax paren => paren.Body as ExpressionSyntax,
                _ => null,
            };

            // Unwrap a cast/conversion such as a => (object)a.Name.
            while (body is CastExpressionSyntax cast)
            {
                body = cast.Expression;
            }

            if (body is MemberAccessExpressionSyntax memberAccess)
            {
                column = memberAccess.Name.Identifier.ValueText;
                return true;
            }

            return false;
        }
    }
}
