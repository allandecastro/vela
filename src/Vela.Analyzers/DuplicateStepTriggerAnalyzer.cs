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
    /// Flags two step registrations in one plugin that share a trigger - message + stage + mode (the entity is fixed
    /// per plugin) - and cannot be told apart: the same <c>FilterOn(...)</c> columns, the same images, and neither
    /// carries a <c>WithStepKey(...)</c> or <c>ForRelationship(...)</c>. Several steps may share a trigger when one of
    /// those differs; two that do not resolve to the same step key and the runtime rejects them ("cannot be told
    /// apart"). Only complete chains (ending in <c>Handle</c>) whose message, stage, filters and images are statically
    /// readable are compared; anything the analyzer cannot read is skipped, so it never guesses.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DuplicateStepTriggerAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.DuplicateStepTrigger;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Duplicate step trigger in a plugin",
            messageFormat: "This plugin already registers a step for {0} with the same filtering attributes and images; give one a WithStepKey(...), or merge them",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Several steps may share a trigger (message + stage + mode + entity) when their filtering attributes, images, relationship scope or step key tell them apart. Two RegisterStep() chains on one trigger that differ in none of these resolve to the same step key and fail at registration. The rank (WithExecutionOrder) does not tell steps apart. Give one a WithStepKey(\"...\"), or keep one step and branch in the handler.");

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
            ["OnAssign"] = "assign",
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

                if (!TryResolveShape(member.Expression, out string shape))
                {
                    continue; // explicitly told apart (step key / relationship), or filters/images not readable
                }

                string key = message + "|" + stage + "|" + mode + "|" + shape;
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

            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                string called = call.MethodName;
                if (message.Length == 0 && TypedTriggerMessage.TryGetValue(called, out string? typed))
                {
                    message = typed!;
                    location = call.Name.GetLocation();
                }
                else if (message.Length == 0 && called == "OnMessage" && TryGetLiteralMessage(call.Invocation, out string permissive))
                {
                    message = permissive;
                    location = call.Name.GetLocation();
                }
                else if (stage.Length == 0 && StageMethods.TryGetValue(called, out string? stageValue))
                {
                    stage = stageValue!;
                }
                else if (called == "Async")
                {
                    mode = "async";
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

        // What tells a step apart from another on the same trigger: its FilterOn(...) columns and its images (alias and
        // columns). Returns false - never compared - for a chain with WithStepKey(...) or ForRelationship(...), which
        // the author distinguished explicitly, or whose filters/images are not literal column selectors.
        private static bool TryResolveShape(ExpressionSyntax? receiver, out string shape)
        {
            shape = string.Empty;
            var filters = new List<string>();
            var images = new List<string>();
            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                switch (call.MethodName)
                {
                    case "WithStepKey":
                    case "ForRelationship":
                        return false;
                    case "FilterOn":
                        if (!TryReadColumns(call.Invocation.ArgumentList.Arguments, 0, filters))
                        {
                            return false;
                        }

                        break;
                    case "WithPreImage":
                    case "WithPostImage":
                    case "WithSubordinatePreImage":
                    case "WithSubordinatePostImage":
                        string kind = call.MethodName.Replace("With", string.Empty).Replace("Image", string.Empty).ToLowerInvariant();
                        SeparatedSyntaxList<ArgumentSyntax> arguments = call.Invocation.ArgumentList.Arguments;
                        string alias = call.MethodName.Replace("With", string.Empty);
                        int first = 0;
                        if (arguments.Count > 0 && arguments[0].Expression is not LambdaExpressionSyntax)
                        {
                            if (arguments[0].Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
                            {
                                return false;
                            }

                            alias = literal.Token.ValueText;
                            first = 1;
                        }

                        var columns = new List<string>();
                        if (!TryReadColumns(arguments, first, columns))
                        {
                            return false;
                        }

                        columns.Sort(System.StringComparer.Ordinal);
                        images.Add(kind + ":" + alias.ToLowerInvariant() + ":" + string.Join(",", columns));
                        break;
                }
            }

            filters.Sort(System.StringComparer.Ordinal);
            images.Sort(System.StringComparer.Ordinal);
            shape = "filter=" + string.Join(",", filters) + "|images=" + string.Join(";", images);
            return true;
        }

        private static bool TryReadColumns(SeparatedSyntaxList<ArgumentSyntax> arguments, int first, List<string> columns)
        {
            for (int i = first; i < arguments.Count; i++)
            {
                if (arguments[i].Expression is not LambdaExpressionSyntax lambda || FluentStep.SelectedMember(lambda) is not MemberAccessExpressionSyntax column)
                {
                    return false;
                }

                columns.Add(column.Name.Identifier.ValueText.ToLowerInvariant());
            }

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
