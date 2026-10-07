using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a Create, Update, CreateMultiple or UpdateMultiple step whose <c>RegisterStep()</c> chain reaches
    /// <c>.Handle(...)</c> without <c>.FilterOn(...)</c>. The builder requires a filtering attribute on these messages
    /// (an unfiltered step fires on every create/update) but can only enforce it when the step is built at runtime, so
    /// this moves the failure to the editor and the build. Only a chain that starts at <c>RegisterStep()</c> in the
    /// same expression is analyzed: a chain split across locals stays silent (under-report, never a false positive).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MissingFilterAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.MissingFilter;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Create/Update step is missing FilterOn(...)",
            messageFormat: "This {0} step has no filtering attribute; {1}",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Create, Update, CreateMultiple and UpdateMultiple steps must declare at least one filtering attribute with FilterOn(...); otherwise building the step fails at runtime.");

        // The typed config interfaces of the messages that require a filter, e.g. ICreatePostOperationConfig<T>.
        private static readonly Regex FilteredConfig = new Regex(
            "^I(Create|Update|CreateMultiple|UpdateMultiple)(PreValidation|PreOperation|PostOperation)Config$",
            RegexOptions.CultureInvariant);

        private static readonly Regex PermissiveConfig = new Regex(
            "^IPermissive(PreValidation|PreOperation|PostOperation)Config$",
            RegexOptions.CultureInvariant);

        private static readonly string[] FilteredMessages = { "Create", "Update", "CreateMultiple", "UpdateMultiple" };

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
            if (invocation.Expression is not MemberAccessExpressionSyntax member || member.Name.Identifier.ValueText != "Handle")
            {
                return;
            }

            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol handle
                || !FluentStep.ImplementsFluent(handle.ContainingType, fluent))
            {
                return;
            }

            Match match = FilteredConfig.Match(handle.ContainingType.Name);
            if (match.Success)
            {
                if (ReachesRegisterStepWithoutFilter(member.Expression))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), match.Groups[1].Value,
                        "add .FilterOn(e => e.Column) before .Handle(...)"));
                }

                return;
            }

            // The permissive OnMessage("Create") path offers no FilterOn at all, so a filter-required message there
            // always fails when the step is built: point at the typed trigger, which does.
            if (PermissiveConfig.IsMatch(handle.ContainingType.Name)
                && FindOnMessageName(member.Expression, context.SemanticModel, context.CancellationToken) is { } message
                && FilteredMessages.FirstOrDefault(m => string.Equals(m, message, System.StringComparison.OrdinalIgnoreCase)) is { } canonical)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), canonical,
                    $"use the typed On{canonical}() trigger, which offers .FilterOn(...), instead of OnMessage(...)"));
            }
        }

        // The compile-time constant message name of the OnMessage("...") call on the chain (a literal or a const), or
        // null when there is none or it is not constant.
        private static string? FindOnMessageName(ExpressionSyntax receiver, SemanticModel model, System.Threading.CancellationToken cancellationToken)
        {
            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                if (call.MethodName == "OnMessage" && call.Arguments.Count >= 1)
                {
                    Optional<object?> value = model.GetConstantValue(call.Arguments[0].Expression, cancellationToken);
                    return value.HasValue ? value.Value as string : null;
                }
            }

            return null;
        }

        // Walks the receiver spine back to the chain root. True only when the root is a RegisterStep() call and no
        // FilterOn(...) was seen on the way.
        private static bool ReachesRegisterStepWithoutFilter(ExpressionSyntax receiver)
        {
            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                switch (call.MethodName)
                {
                    case "FilterOn":
                        return false;
                    case "RegisterStep":
                        return true;
                }
            }

            return false;
        }
    }
}
