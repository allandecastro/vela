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
    /// Flags two step registrations in one plugin that share the same trigger - message + stage + mode (the entity
    /// is fixed per plugin). Dataverse allows only one step per trigger, and the runtime throws
    /// ("registers more than one step for the same trigger"); this surfaces it at build. Only complete chains
    /// (ending in <c>Handle</c>) whose message and stage are statically determinable are compared; anything the
    /// analyzer cannot read is skipped, so it never guesses.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DuplicateStepTriggerAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA008";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Duplicate step trigger in a plugin",
            messageFormat: "This plugin already registers a step for {0}; merge them (combine filtering attributes) or use a different stage/mode",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Dataverse allows only one plugin step per trigger (message + stage + mode + entity). Two RegisterStep() chains with the same trigger in one plugin fail at registration.");

        private static readonly ImmutableDictionary<string, string> TypedTriggerMessage = new Dictionary<string, string>
        {
            ["OnCreate"] = "create",
            ["OnUpdate"] = "update",
            ["OnDelete"] = "delete",
            ["OnCreateMultiple"] = "createmultiple",
            ["OnUpdateMultiple"] = "updatemultiple",
            ["OnRetrieve"] = "retrieve",
            ["OnRetrieveMultiple"] = "retrievemultiple",
            ["OnAssociate"] = "associate",
            ["OnDisassociate"] = "disassociate",
            ["OnMerge"] = "merge",
        }.ToImmutableDictionary();

        private static readonly ImmutableDictionary<string, string> StageMethods = new Dictionary<string, string>
        {
            ["PreValidation"] = "prevalidation",
            ["PreOperation"] = "preoperation",
            ["PostOperation"] = "postoperation",
        }.ToImmutableDictionary();

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

                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeMethod(nodeContext, fluent), SyntaxKind.MethodDeclaration);
            });
        }

        private static void AnalyzeMethod(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent)
        {
            var method = (MethodDeclarationSyntax)context.Node;
            if (method.Body is null && method.ExpressionBody is null)
            {
                return;
            }

            // Steps are registered from the RegisterPluginSteps() override; only look there.
            if (method.Identifier.ValueText != "RegisterPluginSteps")
            {
                return;
            }

            var seen = new Dictionary<string, string>(System.StringComparer.Ordinal); // trigger key -> display
            foreach (InvocationExpressionSyntax invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member || member.Name.Identifier.ValueText != "Handle")
                {
                    continue;
                }

                if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
                {
                    continue;
                }

                if (!TryResolveTrigger(member.Expression, out string message, out string stage, out string mode, out Location location, out string display))
                {
                    continue; // undeterminable trigger - skip rather than guess
                }

                string key = message + "|" + stage + "|" + mode;
                if (seen.ContainsKey(key))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, location, display));
                }
                else
                {
                    seen[key] = display;
                }
            }
        }

        // Walks the receiver spine of a Handle(...) call to read the trigger: message (typed On*/permissive
        // OnMessage), stage, and mode (Async, else Sync). Returns false when message or stage is undeterminable.
        private static bool TryResolveTrigger(ExpressionSyntax? receiver, out string message, out string stage, out string mode, out Location location, out string display)
        {
            message = string.Empty;
            stage = string.Empty;
            mode = "sync";
            location = Location.None;
            display = string.Empty;

            ExpressionSyntax? current = receiver;
            while (current is not null)
            {
                switch (current)
                {
                    case InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax ma:
                        string called = ma.Name.Identifier.ValueText;
                        if (message.Length == 0 && TypedTriggerMessage.TryGetValue(called, out string? typed))
                        {
                            message = typed!;
                            location = ma.Name.GetLocation();
                        }
                        else if (message.Length == 0 && called == "OnMessage" && TryGetLiteralMessage(inv, out string permissive))
                        {
                            message = permissive;
                            location = ma.Name.GetLocation();
                        }
                        else if (stage.Length == 0 && StageMethods.TryGetValue(called, out string? stageValue))
                        {
                            stage = stageValue!;
                        }
                        else if (called == "Async")
                        {
                            mode = "async";
                        }

                        current = ma.Expression;
                        break;
                    case MemberAccessExpressionSyntax ma2:
                        current = ma2.Expression;
                        break;
                    case ParenthesizedExpressionSyntax paren:
                        current = paren.Expression;
                        break;
                    default:
                        current = null;
                        break;
                }
            }

            // A trigger is only compared when BOTH its message and stage are resolvable from the chain. The fluent API
            // ALWAYS requires an explicit stage (every On*/OnMessage path goes through PreValidation/PreOperation/
            // PostOperation), so an empty stage here means the chain shape could not be parsed, not that a "default
            // stage" applies - and comparing an unparsed trigger would risk a false positive. Such a step is skipped;
            // a genuine duplicate is still rejected at registration by the framework (#309 M4: documented, not a
            // resolvable static default).
            if (message.Length == 0 || stage.Length == 0)
            {
                return false;
            }

            display = $"message '{message}' at {stage} ({mode})";
            return true;
        }

        private static bool TryGetLiteralMessage(InvocationExpressionSyntax invocation, out string message)
        {
            message = string.Empty;
            SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count != 1 || arguments[0].Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return false;
            }

            message = literal.Token.ValueText.Trim().ToLowerInvariant();
            return message.Length > 0;
        }
    }
}
