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
    /// Flags a generated relationship navigation property (one carrying <c>[RelationshipSchemaName(...)]</c>) used
    /// where a column is expected - a column selector such as <c>FilterOn</c>, <c>Select</c>, <c>Where</c>,
    /// <c>OrderBy</c>, <c>WithPreImage</c> / <c>WithPostImage</c>. A relationship is not a column: at runtime the
    /// resolver rejects it (it would otherwise silently build a bogus column from the property name). Load related
    /// records with a relationship-aware read or a deep insert instead.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RelationshipColumnSelectorAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA010";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Relationship used as a column",
            messageFormat: "'{0}' is a relationship, not a column; it cannot be used in {1}",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A generated relationship navigation property is not a storable column. Using it in a column selector (FilterOn/Select/Where/OrderBy/WithPreImage/WithPostImage) fails at runtime. Load related records with a relationship-aware read or deep insert.");

        // The column-selector methods on Vela's step builder and typed query. A relationship in any of these is wrong.
        private static readonly ImmutableHashSet<string> SelectorMethods = ImmutableHashSet.Create(
            "FilterOn", "Select", "Where", "WhereAll", "WhereAny", "WhereColumns",
            "OrderBy", "OrderByDescending", "WithPreImage", "WithPostImage");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                // Gate on a Vela marker type so we do not resolve symbols for every LINQ Select/Where/OrderBy in a
                // compilation that does not reference Vela at all (the namespace filter still runs per invocation,
                // but only after this cheap up-front check clears).
                if (start.Compilation.GetTypeByMetadataName("Vela.Steps.IFluentStep") is null)
                {
                    return;
                }

                start.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            });
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                return;
            }

            string methodName = member.Name.Identifier.ValueText;
            if (!SelectorMethods.Contains(methodName))
            {
                return;
            }

            // Only Vela's own selectors - never ordinary LINQ Select/Where/OrderBy on IEnumerable.
            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
                !IsVelaNamespace(method.ContainingType?.ContainingNamespace))
            {
                return;
            }

            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                if (argument.Expression is not LambdaExpressionSyntax lambda)
                {
                    continue;
                }

                ExpressionSyntax? body = UnwrapLambdaBody(lambda);
                if (body is not MemberAccessExpressionSyntax navAccess)
                {
                    continue;
                }

                if (context.SemanticModel.GetSymbolInfo(navAccess, context.CancellationToken).Symbol is IPropertySymbol property &&
                    IsRelationshipNav(property))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, navAccess.GetLocation(), property.Name, methodName));
                }
            }
        }

        private static ExpressionSyntax? UnwrapLambdaBody(LambdaExpressionSyntax lambda)
        {
            if (lambda.Body is not ExpressionSyntax body)
            {
                return null;
            }

            // a => (object)a.Nav / a => a.Nav as X — see through a leading cast (CastExpression) or `as`
            // (an AsExpression BinaryExpression) to the member access.
            while (true)
            {
                switch (body)
                {
                    case CastExpressionSyntax cast:
                        body = cast.Expression;
                        continue;
                    case BinaryExpressionSyntax asExpr when asExpr.IsKind(SyntaxKind.AsExpression):
                        body = asExpr.Left;
                        continue;
                }

                break;
            }

            return body;
        }

        private static bool IsRelationshipNav(IPropertySymbol property) =>
            property.GetAttributes().Any(a => a.AttributeClass?.Name == "RelationshipSchemaNameAttribute");

        private static bool IsVelaNamespace(INamespaceSymbol? ns)
        {
            for (INamespaceSymbol? current = ns; current is not null && !current.IsGlobalNamespace; current = current.ContainingNamespace)
            {
                if (current.Name == "Vela" && (current.ContainingNamespace?.IsGlobalNamespace ?? false))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
