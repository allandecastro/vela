using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a step registration chain that stops before <c>.Handle(...)</c>. The redesigned builder puts Handle
    /// last (returning void), so a step is required to end with it. Any <c>RegisterStep()....</c> statement whose
    /// expression is still a fluent builder type (implements <c>Vela.Steps.IFluentStep</c>) means the handler was
    /// never bound. Reported as an error so it fails in the editor and at build.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class StepCompletionAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA002";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Step registration is missing Handle(...)",
            messageFormat: "This step never binds a handler; end the chain with .Handle(...)",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A RegisterStep() chain must end with .Handle(...). A statement whose value is still a Vela.Steps fluent builder means the handler was never bound.");

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

                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeStatement(nodeContext, fluent), SyntaxKind.ExpressionStatement);
            });
        }

        private static void AnalyzeStatement(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent)
        {
            ExpressionStatementSyntax statement = (ExpressionStatementSyntax)context.Node;

            // Only care about a builder chain whose value is discarded as a statement.
            ITypeSymbol? type = context.SemanticModel.GetTypeInfo(statement.Expression, context.CancellationToken).Type;
            if (type is null)
            {
                return;
            }

            if (ImplementsFluent(type, fluent))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, statement.Expression.GetLocation()));
            }
        }

        private static bool ImplementsFluent(ITypeSymbol type, INamedTypeSymbol fluent)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, fluent))
            {
                return true;
            }

            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, fluent))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
