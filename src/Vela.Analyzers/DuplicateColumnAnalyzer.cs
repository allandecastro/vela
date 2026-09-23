using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags the same column listed twice in a single <c>FilterOn(...)</c>, <c>WithPreImage(...)</c>, or
    /// <c>WithPostImage(...)</c> call (for example <c>FilterOn(a =&gt; a.Name, a =&gt; a.Name)</c>). A repeated column
    /// adds nothing and is almost always a copy/paste slip, so it is reported at build. Only lambda column selectors
    /// are considered; a leading image alias string is ignored.
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
            description: "The same column appears more than once in a single FilterOn / WithPreImage / WithPostImage call. A repeated column has no effect and is usually a copy/paste mistake.");

        private static readonly ImmutableHashSet<string> ColumnListMethods = ImmutableHashSet.Create(
            "FilterOn",
            "WithPreImage",
            "WithPostImage");

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

            if (!ColumnListMethods.Contains(member.Name.Identifier.ValueText))
            {
                return;
            }

            if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
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
