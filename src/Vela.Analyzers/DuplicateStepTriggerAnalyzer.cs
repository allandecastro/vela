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
    /// per plugin) - and cannot be told apart: the same <c>FilterOn(...)</c> columns, the same images and the same
    /// <c>ForRelationship(...)</c> scope, and neither carries a <c>WithStepKey(...)</c>. Several steps may share a
    /// trigger when one of those differs; two that do not resolve to the same step key and the runtime rejects them
    /// ("cannot be told apart"). Also flags a literal <c>WithStepKey(...)</c> used twice in the plugin, which the
    /// runtime rejects whatever the triggers. A type-stated trigger is keyed by the sdkmessage it registers on
    /// (<c>On&lt;SendEmailRequest&gt;()</c> and <c>On&lt;SendFaxRequest&gt;()</c> both on <c>Send</c>). Only complete
    /// chains (ending in <c>Handle</c>) whose message, stage, filters, images, relationships and key are statically
    /// readable are compared; anything the analyzer cannot read is skipped, so it never guesses.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DuplicateStepTriggerAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.DuplicateStepTrigger;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Duplicate step trigger in a plugin",
            messageFormat: "This plugin already registers a step for {0} with the same filtering attributes, images and relationship scope; give one a WithStepKey(...), or merge them",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Several steps may share a trigger (message + stage + mode + entity) when their filtering attributes, images, relationship scope or step key tell them apart. Two RegisterStep() chains on one trigger that differ in none of these resolve to the same step key and fail at registration. The rank (WithExecutionOrder) does not tell steps apart. Give one a WithStepKey(\"...\"), or keep one step and branch in the handler.");

        private static readonly ImmutableDictionary<string, string> StageMethods = new Dictionary<string, string>
        {
            ["PreValidation"] = "prevalidation",
            ["PreOperation"] = "preoperation",
            ["PostOperation"] = "postoperation",
        }.ToImmutableDictionary();

        // Same rule, the step key case: the runtime rejects a step key used twice within a plugin, on any trigger.
        private static readonly DiagnosticDescriptor DuplicateKeyRule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Duplicate step key in a plugin",
            messageFormat: "This plugin already registers a step with the step key '{0}'; step keys must be unique within a plugin",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A WithStepKey(\"...\") names the Dataverse step a chain is deployed as. Two chains of one plugin with the same key (compared case-insensitively) collide, whatever their triggers, and fail at registration. Give each step its own key.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule, DuplicateKeyRule);

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
            var keys = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);   // literal step keys
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

                if (!TryResolveShape(member.Expression, context.SemanticModel, context.CancellationToken, out string shape, out string? stepKey, out Location keyLocation))
                {
                    continue; // a filter, image, relationship or step key not readable
                }

                if (stepKey is not null)
                {
                    // A keyed step is deployed under its key alone: only another step with the same key collides.
                    if (!keys.Add(stepKey))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(DuplicateKeyRule, keyLocation, stepKey));
                    }

                    continue;
                }

                if (!TryResolveTrigger(member.Expression, context.SemanticModel, context.CancellationToken, out string message, out string stage, out string mode, out Location location, out string display))
                {
                    continue; // undeterminable trigger - skip rather than guess
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

        // Walks the receiver spine of a Handle(...) call to read the trigger: message (typed On*, or the type of a
        // type-stated On<TRequest>()/OnMessage<TMessage>()), stage, and mode (Async, else Sync). Returns false when
        // message or stage is undeterminable.
        private static bool TryResolveTrigger(ExpressionSyntax? receiver, SemanticModel model, System.Threading.CancellationToken cancellationToken, out string message, out string stage, out string mode, out Location location, out string display)
        {
            message = string.Empty;
            stage = string.Empty;
            mode = "sync";
            location = Location.None;
            display = string.Empty;

            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                string called = call.MethodName;
                if (message.Length == 0 && call.Name is not GenericNameSyntax && FluentStep.TypedTriggerMessage(called) is { } typed)
                {
                    // Every typed On{Message}() trigger, keyed by the sdkmessage it registers on, as the builder does
                    // (OnSendEmail() and OnSendFax() both register on Send).
                    message = Vela.Steps.SdkMessageNames.ToSdkMessage(typed).ToLowerInvariant();
                    location = call.Name.GetLocation();
                }
                else if (message.Length == 0 && FluentStep.TypeStatedSdkMessage(call, model, cancellationToken) is { } typeStated)
                {
                    message = typeStated.ToLowerInvariant();
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

                // OnAsyncOnly<TRequest>() has no mode call: its steps run asynchronously.
                if (called == "OnAsyncOnly")
                {
                    mode = "async";
                }
            }

            // A trigger is only compared when BOTH its message and stage are resolvable from the chain. The fluent API
            // ALWAYS requires an explicit stage (every On*/OnMessage<T> path goes through PreValidation/PreOperation/
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

        // What tells a step apart from another on the same trigger: its FilterOn(...) columns, its images (kind, alias
        // and columns) and its ForRelationship(...) scope, or its WithStepKey(...), which alone identifies a keyed step
        // (stepKey, trimmed, with the location of its argument). Returns false - never compared - for a chain whose key
        // is not a constant string or, for a keyless chain, whose filters or images are not literal column selectors
        // or whose relationships are not constant strings.
        private static bool TryResolveShape(ExpressionSyntax? receiver, SemanticModel model, System.Threading.CancellationToken cancellationToken, out string shape, out string? stepKey, out Location keyLocation)
        {
            shape = string.Empty;
            stepKey = null;
            keyLocation = Location.None;
            var filters = new List<string>();
            var images = new List<string>();
            var relationships = new List<string>();
            bool readable = true;
            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                SeparatedSyntaxList<ArgumentSyntax> arguments = call.Invocation.ArgumentList.Arguments;
                switch (call.MethodName)
                {
                    case "WithStepKey":
                        if (arguments.Count != 1 || ConstantString(arguments[0].Expression, model, cancellationToken) is not { } key)
                        {
                            return false;
                        }

                        stepKey = key.Trim();
                        keyLocation = arguments[0].GetLocation();
                        break;
                    case "ForRelationship":
                        foreach (ArgumentSyntax argument in arguments)
                        {
                            if (ConstantString(argument.Expression, model, cancellationToken) is not { } relationship)
                            {
                                readable = false;
                                break;
                            }

                            relationships.Add(relationship.Trim().ToLowerInvariant());
                        }

                        break;
                    case "FilterOn":
                        readable &= TryReadColumns(arguments, 0, filters);

                        break;
                    case "WithPreImage":
                    case "WithPostImage":
                    case "WithSubordinatePreImage":
                    case "WithSubordinatePostImage":
                        string kind = call.MethodName.Replace("With", string.Empty).Replace("Image", string.Empty).ToLowerInvariant();
                        string alias = call.MethodName.Replace("With", string.Empty);
                        int first = 0;
                        if (arguments.Count > 0 && arguments[0].Expression is not LambdaExpressionSyntax)
                        {
                            if (arguments[0].Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
                            {
                                readable = false;
                                break;
                            }

                            alias = literal.Token.ValueText;
                            first = 1;
                        }

                        var columns = new List<string>();
                        if (!TryReadColumns(arguments, first, columns))
                        {
                            readable = false;
                            break;
                        }

                        columns.Sort(System.StringComparer.Ordinal);
                        images.Add(kind + ":" + alias.ToLowerInvariant() + ":" + string.Join(",", columns));
                        break;
                }
            }

            if (stepKey is not null)
            {
                return true;
            }

            if (!readable)
            {
                return false;
            }

            filters.Sort(System.StringComparer.Ordinal);
            images.Sort(System.StringComparer.Ordinal);
            relationships.Sort(System.StringComparer.Ordinal);
            shape = "filter=" + string.Join(",", filters) + "|images=" + string.Join(";", images) + "|rel=" + string.Join(",", relationships);
            return true;
        }

        private static string? ConstantString(ExpressionSyntax expression, SemanticModel model, System.Threading.CancellationToken cancellationToken)
        {
            Optional<object?> constant = model.GetConstantValue(expression, cancellationToken);
            return constant.HasValue && constant.Value is string text ? text : null;
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
    }
}
