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
    /// <c>WithPostImage(...)</c>, typed-query <c>Select(...)</c>, or typed read <c>Retrieve&lt;T&gt;(...)</c> /
    /// <c>RetrieveByKey&lt;T&gt;(...)</c> call (for example <c>FilterOn(a =&gt; a.Name, a =&gt; a.Name)</c> or
    /// <c>Query.For&lt;T&gt;().Select(c =&gt; c.Name, c =&gt; c.Name)</c>).
    /// A repeated column adds nothing and is almost always a copy/paste slip, so it is reported at build. Only lambda
    /// column selectors are considered; a leading image alias string is ignored. The relationship selector
    /// <c>Include(...)</c> is a different method (handled by VELA010) and is not considered here.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DuplicateColumnAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.DuplicateColumn;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Duplicate column in a selector list",
            messageFormat: "Column '{0}' is listed more than once in this call; list each column at most once",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "The same column appears more than once in a single FilterOn / With...Image / typed-query Select / typed Retrieve call. A repeated column has no effect and is usually a copy/paste mistake.");

        // Step-authoring column-list methods, gated on Vela.Steps.IFluentStep.
        private static readonly ImmutableHashSet<string> FluentColumnMethods = ImmutableHashSet.Create(
            "FilterOn",
            "WithPreImage",
            "WithPostImage",
            "WithSubordinatePreImage",
            "WithSubordinatePostImage");

        // The typed-query column selector Query.For<T>().Select(...); gated on the Vela.Querying namespace so an
        // unrelated Select (LINQ, a user type) is ignored. Include (relationship selector) is a different name.
        private const string SelectMethod = "Select";

        // The typed reads service.Retrieve<T>(id, ...) and service.RetrieveByKey<T>(key, ...); gated on the
        // Vela.Context namespace so IOrganizationService.Retrieve (and any other Retrieve) is ignored.
        private static readonly ImmutableHashSet<string> ReadMethods = ImmutableHashSet.Create("Retrieve", "RetrieveByKey");

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
            bool isRead = ReadMethods.Contains(methodName);
            if (!isSelect && !isRead && !FluentColumnMethods.Contains(methodName))
            {
                return;
            }

            // Scope to Vela's own methods: the typed-query Select and the typed reads are recognised by their
            // declaring namespace, the step-authoring methods by the IFluentStep marker, so a same-named method on an
            // unrelated type is ignored.
            bool recognised = isSelect || isRead
                ? IsDeclaredIn(context.SemanticModel, invocation, isSelect ? "Vela.Querying" : "Vela.Context", context.CancellationToken)
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

        // True when the invoked method is declared in the given Vela namespace: Vela.Querying for the typed-query
        // column selector (TypedQuery<T>.Select / TypedLink<T>.Select / the QueryExpression Select extension),
        // Vela.Context for the typed reads. Anything else of that name (LINQ Select, IOrganizationService.Retrieve, a
        // user type) is not a duplicate-column concern here.
        private static bool IsDeclaredIn(SemanticModel model, InvocationExpressionSyntax invocation, string ns, System.Threading.CancellationToken cancellationToken)
        {
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            {
                return false;
            }

            INamespaceSymbol? declaring = method.ContainingType?.ContainingNamespace;
            return declaring is not null && declaring.ToDisplayString() == ns;
        }

        // A column selector is a lambda whose body is a member access on the parameter, e.g. a => a.Name. Returns
        // the selected member name; false for anything that is not such a selector.
        private static bool TryGetSelectedColumn(ExpressionSyntax expression, out string column)
        {
            // A column selector is a member access DIRECTLY on the lambda parameter (a => a.Name), seen through casts.
            // A nested path (a => a.Parent.Name) reads a member of a RELATED entity, not a column on this one - comparing
            // by the leaf name alone would wrongly flag a.Name and a.Parent.Name as the same column (#309 L2).
            column = expression is LambdaExpressionSyntax lambda && FluentStep.SelectedMember(lambda) is { } member
                ? member.Name.Identifier.ValueText
                : string.Empty;
            return column.Length != 0;
        }
    }
}
