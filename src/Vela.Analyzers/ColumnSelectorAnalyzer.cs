using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a Vela column selector (an <c>Expression&lt;Func&lt;TEntity, object&gt;&gt;</c> where
    /// <c>TEntity : Microsoft.Xrm.Sdk.Entity</c>, as used by <c>FilteringAttributes</c>, pre/post image
    /// <c>Attributes(...)</c>, typed queries and the change/attribute helpers) that targets a member which is not a
    /// Dataverse column. Members declared on <c>Microsoft.Xrm.Sdk.Entity</c> itself (for example <c>LogicalName</c>,
    /// <c>Id</c>, <c>Attributes</c>, <c>RelatedEntities</c>) compile but are not columns, so selecting one registers
    /// or queries a meaningless attribute. Reported as an error so it fails in the editor and at build time.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ColumnSelectorAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA001";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Column selector targets a member that is not a Dataverse column",
            messageFormat: "'{0}' is not a Dataverse column; a Vela column selector must target a generated early-bound column property",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A Vela column selector (Expression<Func<TEntity, object>>) must target a real column. Members declared on Microsoft.Xrm.Sdk.Entity (LogicalName, Id, Attributes, RelatedEntities, ...) are not columns.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol? entityType = start.Compilation.GetTypeByMetadataName("Microsoft.Xrm.Sdk.Entity");
                INamedTypeSymbol? expressionOfT = start.Compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
                INamedTypeSymbol? funcOfT2 = start.Compilation.GetTypeByMetadataName("System.Func`2");
                if (entityType is null || expressionOfT is null || funcOfT2 is null)
                {
                    return;
                }

                start.RegisterSyntaxNodeAction(
                    nodeContext => AnalyzeLambda(nodeContext, entityType, expressionOfT, funcOfT2),
                    SyntaxKind.SimpleLambdaExpression,
                    SyntaxKind.ParenthesizedLambdaExpression);
            });
        }

        private static void AnalyzeLambda(SyntaxNodeAnalysisContext context, INamedTypeSymbol entityType, INamedTypeSymbol expressionOfT, INamedTypeSymbol funcOfT2)
        {
            LambdaExpressionSyntax lambda = (LambdaExpressionSyntax)context.Node;

            // Cheap syntactic gate FIRST, before any semantic-model call: the lambda body must resolve to a member
            // access on the parameter, seeing through a leading cast (`(object)a.X`), an `as` (`a.X as object`) and
            // nested/double casts - matching ImageProjectionGenerator so a cast selector is analysed, not dropped
            // (#309 L1). Running this before GetTypeInfo avoids the expensive conversion lookup on non-selector
            // lambdas (a plain LINQ Func<...> body) (#309 P1).
            if (lambda.Body is not ExpressionSyntax lambdaBody)
            {
                return;
            }

            ExpressionSyntax body = UnwrapConversions(lambdaBody);
            if (body is not MemberAccessExpressionSyntax memberAccess)
            {
                return;
            }

            // Only Vela column selectors: the lambda is converted to Expression<Func<TEntity, object>>, TEntity : Entity.
            // A plain LINQ Func<...> lambda (not an expression tree) is not a column selector and is left alone.
            if (context.SemanticModel.GetTypeInfo(lambda, context.CancellationToken).ConvertedType is not INamedTypeSymbol converted
                || !SymbolEqualityComparer.Default.Equals(converted.OriginalDefinition, expressionOfT)
                || converted.TypeArguments.Length != 1
                || converted.TypeArguments[0] is not INamedTypeSymbol func
                || !SymbolEqualityComparer.Default.Equals(func.OriginalDefinition, funcOfT2)
                || func.TypeArguments.Length != 2
                || func.TypeArguments[0] is not INamedTypeSymbol entityArg
                || !InheritsFrom(entityArg, entityType))
            {
                return;
            }

            ISymbol? symbol = context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol;
            if (symbol?.ContainingType is null)
            {
                return;
            }

            // A member declared on Entity itself (never a real column) - as opposed to a generated/authored model
            // property declared on the derived entity type.
            if (SymbolEqualityComparer.Default.Equals(symbol.ContainingType.OriginalDefinition, entityType))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.Name.GetLocation(), symbol.Name));
            }
        }

        // Sees through leading conversions on a column-selector body: an explicit cast ((object)a.X), an `as`
        // (a.X as object), and nesting of the two (double cast). Mirrors ImageProjectionGenerator so the analyzer and
        // the source generator agree on which bodies are column selectors.
        private static ExpressionSyntax UnwrapConversions(ExpressionSyntax body)
        {
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

                return body;
            }
        }

        private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
