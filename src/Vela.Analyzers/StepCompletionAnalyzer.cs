using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a step registration chain that stops before <c>.Handle(...)</c>. The redesigned builder puts Handle
    /// last (returning void), so a step is required to end with it. Any <c>RegisterStep()....</c> statement - or
    /// expression body of a void member, such as <c>RegisterPluginSteps() =&gt; RegisterStep()...;</c> - whose value
    /// is still a fluent builder type (implements <c>Vela.Steps.IFluentStep</c>) means the handler was never bound.
    /// Reported as an error so it fails in the editor and at build.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class StepCompletionAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.StepCompletion;

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

                // Deliberately only a discarded value (a `RegisterStep()....;` statement or void expression body, the common
                // mistake). We intentionally do NOT flag a chain captured in a local (`var c = RegisterStep()...;`)
                // or a field/return: a user may legitimately split the chain and complete it later
                // (`c.FilterOn(...).Handle(...)`). Because this rule is an error, flagging those would fail the
                // build on valid code (a false positive), which is worse than missing the rare split-and-drop case.
                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeStatement(nodeContext, fluent), SyntaxKind.ExpressionStatement);

                // The expression body of a void member (`protected override void RegisterPluginSteps() => RegisterStep()...;`,
                // the form most samples use) discards its value exactly like a statement does.
                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeExpressionBody(nodeContext, fluent), SyntaxKind.ArrowExpressionClause);
            });
        }

        private static void AnalyzeStatement(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent)
            => ReportIfBuilder(context, ((ExpressionStatementSyntax)context.Node).Expression, fluent);

        private static void AnalyzeExpressionBody(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent)
        {
            ArrowExpressionClauseSyntax body = (ArrowExpressionClauseSyntax)context.Node;

            // Only a member whose value is discarded: a void method or local function, or a setter/constructor. A
            // property getter or a method returning the builder hands the chain on (it may be completed later).
            bool discarded = body.Parent switch
            {
                MethodDeclarationSyntax or LocalFunctionStatementSyntax =>
                    context.SemanticModel.GetDeclaredSymbol(body.Parent, context.CancellationToken) is IMethodSymbol method && method.ReturnsVoid,
                AccessorDeclarationSyntax accessor => !accessor.IsKind(SyntaxKind.GetAccessorDeclaration),
                ConstructorDeclarationSyntax => true,
                _ => false,
            };

            if (discarded)
            {
                ReportIfBuilder(context, body.Expression, fluent);
            }
        }

        // Reports a discarded expression whose value is still a fluent builder (the handler was never bound).
        private static void ReportIfBuilder(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, INamedTypeSymbol fluent)
        {
            ITypeSymbol? type = context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type;
            if (type is not null && FluentStep.ImplementsFluent(type, fluent))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, expression.GetLocation()));
            }
        }
    }
}
