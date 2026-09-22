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
        public const string DiagnosticId = "VELA013";

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
            context.RegisterSyntaxNodeAction(AnalyzeCreation, SyntaxKind.ObjectCreationExpression);
        }

        private static void AnalyzeCreation(SyntaxNodeAnalysisContext context)
        {
            ObjectCreationExpressionSyntax creation = (ObjectCreationExpressionSyntax)context.Node;

            // A single `true` argument is the retrieve-all overload: new ColumnSet(true) / new ColumnSet(allColumns: true).
            SeparatedSyntaxList<ArgumentSyntax>? arguments = creation.ArgumentList?.Arguments;
            if (arguments is not { Count: 1 } || !arguments.Value[0].Expression.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                return;
            }

            if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is not INamedTypeSymbol type ||
                type.Name != "ColumnSet" || type.ContainingNamespace?.ToDisplayString() != "Microsoft.Xrm.Sdk.Query")
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
        }
    }
}
