using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags <c>new ColumnSet(true)</c> (retrieve every column) inside a plugin / Custom API type. Fetching all columns
    /// moves far more data than a plugin needs and defeats query folding; retrieve only the columns you use. The
    /// argument is read as a constant, so <c>new ColumnSet(AllColumns)</c> over a <c>const bool</c> is caught too. Code
    /// outside a plugin type (tests, tools) is left alone, like VELA019, and so is the explicit Vela <c>SelectAll()</c>
    /// (it states the intent).
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
            context.RegisterCompilationStartAction(start =>
            {
                PluginTypes? plugins = PluginTypes.From(start.Compilation);
                if (plugins is null)
                {
                    return; // neither the Dataverse SDK nor Vela: no plugin type to analyze
                }

                start.RegisterSyntaxNodeAction(
                    c => AnalyzeCreation(c, plugins),
                    SyntaxKind.ObjectCreationExpression,
                    SyntaxKind.ImplicitObjectCreationExpression);
            });
        }

        private static void AnalyzeCreation(SyntaxNodeAnalysisContext context, PluginTypes plugins)
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
            if (!IsRetrieveAllArgument(argumentList, context) && !IsAllColumnsInitializer(initializer, context))
            {
                return;
            }

            if (context.SemanticModel.GetTypeInfo((ExpressionSyntax)context.Node, context.CancellationToken).Type is not INamedTypeSymbol type ||
                type.Name != "ColumnSet" || type.ContainingNamespace?.ToDisplayString() != "Microsoft.Xrm.Sdk.Query" ||
                !plugins.Encloses(context.Node, context.SemanticModel, context.CancellationToken))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation()));
        }

        private static bool IsRetrieveAllArgument(ArgumentListSyntax? argumentList, SyntaxNodeAnalysisContext context)
        {
            SeparatedSyntaxList<ArgumentSyntax>? arguments = argumentList?.Arguments;
            return arguments is { Count: 1 } && IsConstantTrue(arguments.Value[0].Expression, context);
        }

        private static bool IsAllColumnsInitializer(InitializerExpressionSyntax? initializer, SyntaxNodeAnalysisContext context)
        {
            if (initializer is null)
            {
                return false;
            }

            foreach (ExpressionSyntax expression in initializer.Expressions)
            {
                if (expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier: { ValueText: "AllColumns" } } } assignment &&
                    IsConstantTrue(assignment.Right, context))
                {
                    return true;
                }
            }

            return false;
        }

        // The literal `true`, a `const bool` that is true, or any other constant expression that folds to true.
        private static bool IsConstantTrue(ExpressionSyntax expression, SyntaxNodeAnalysisContext context)
            => context.SemanticModel.GetConstantValue(expression, context.CancellationToken) is { HasValue: true, Value: true };
    }
}
