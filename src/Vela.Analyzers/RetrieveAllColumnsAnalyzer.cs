using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags <c>new ColumnSet(true)</c> (retrieve every column). Fetching all columns moves far more data than a
    /// plugin needs and defeats query folding; retrieve only the columns you use. The explicit Vela
    /// <c>SelectAll()</c> is left alone (it states the intent).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RetrieveAllColumnsAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.RetrieveAllColumns;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Retrieving all columns",
            messageFormat: "new ColumnSet(true) retrieves every column; request only the columns you use",
            category: "Vela.Performance",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Retrieving all columns moves more data than needed and hurts performance. Pass the specific columns to ColumnSet, or use the typed Select(...).");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(
                AnalyzeCreation,
                SyntaxKind.ObjectCreationExpression,
                SyntaxKind.ImplicitObjectCreationExpression);
        }

        private static void AnalyzeCreation(SyntaxNodeAnalysisContext context)
        {
            // Handle both the explicit `new ColumnSet(...)` and the target-typed `new(...)` forms.
            ArgumentListSyntax? argumentList;
            InitializerExpressionSyntax? initializer;
            switch (context.Node)
            {
                case ObjectCreationExpressionSyntax explicitCreation:
                    argumentList = explicitCreation.ArgumentList;
                    initializer = explicitCreation.Initializer;
                    break;
                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    argumentList = implicitCreation.ArgumentList;
                    initializer = implicitCreation.Initializer;
                    break;
                default:
                    return;
            }

            // Retrieve-all is expressed either as the single `true` constructor argument (new ColumnSet(true) /
            // new ColumnSet(allColumns: true)) or as an object initializer setting AllColumns = true.
            if (!IsRetrieveAllArgument(argumentList) && !IsAllColumnsInitializer(initializer))
            {
                return;
            }

            if (context.SemanticModel.GetTypeInfo((ExpressionSyntax)context.Node, context.CancellationToken).Type is not INamedTypeSymbol type ||
                type.Name != "ColumnSet" || type.ContainingNamespace?.ToDisplayString() != "Microsoft.Xrm.Sdk.Query")
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation()));
        }

        private static bool IsRetrieveAllArgument(ArgumentListSyntax? argumentList)
        {
            SeparatedSyntaxList<ArgumentSyntax>? arguments = argumentList?.Arguments;
            return arguments is { Count: 1 } && arguments.Value[0].Expression.IsKind(SyntaxKind.TrueLiteralExpression);
        }

        private static bool IsAllColumnsInitializer(InitializerExpressionSyntax? initializer)
        {
            if (initializer is null)
            {
                return false;
            }

            foreach (ExpressionSyntax expression in initializer.Expressions)
            {
                if (expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier: { ValueText: "AllColumns" } } } assignment &&
                    assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
