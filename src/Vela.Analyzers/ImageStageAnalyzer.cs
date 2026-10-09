using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a pre/post-image that the message can never provide: a <c>WithPreImage(...)</c> on a Create or
    /// CreateMultiple step (there is no prior row state before a create), a <c>WithPostImage(...)</c> on a Delete
    /// step (the row no longer exists afterwards), or any image on a message that supports none (RouteTo). The typed
    /// triggers (<c>OnCreate()</c>, ...) already prevent most of this through the builder's type-state, so this
    /// targets the permissive <c>OnMessage(...)</c> path where the image methods are exposed unconditionally. The
    /// rules come from <see cref="Vela.Steps.StepImageRules"/>, shared with the runtime (StepBuilder.EnsureImageAllowed)
    /// and the CLI scanner; the stage rules are left to those, since the stage is not always visible here.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ImageStageAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.ImageStage;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Image not valid for the step's message",
            messageFormat: "{0} is not valid on a {1} step: {2}",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A pre-image on Create (no prior row state), a post-image on Delete (the row is gone) or any image on a message that supports none (RouteTo) can never be provided. The runtime and the CLI reject these; use the correct image for the message.");

        // Display names for the messages whose image rules are modelled (lower-case key), so a permissive
        // OnMessage("create") reports as "Create".
        private static readonly ImmutableDictionary<string, string> MessageDisplayNames = new Dictionary<string, string>
        {
            ["create"] = "Create",
            ["createmultiple"] = "CreateMultiple",
            ["update"] = "Update",
            ["updatemultiple"] = "UpdateMultiple",
            ["delete"] = "Delete",
            ["routeto"] = "RouteTo",
        }.ToImmutableDictionary();

        // Typed trigger method names mapped to their message (OnCreate -> Create, ...).
        private static readonly ImmutableDictionary<string, string> TypedTriggerMessage = new Dictionary<string, string>
        {
            ["OnCreate"] = "Create",
            ["OnCreateMultiple"] = "CreateMultiple",
            ["OnUpdate"] = "Update",
            ["OnUpdateMultiple"] = "UpdateMultiple",
            ["OnDelete"] = "Delete",
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

            string name = member.Name.Identifier.ValueText;
            bool isPreImage = name == "WithPreImage";
            bool isPostImage = name == "WithPostImage";
            if (!isPreImage && !isPostImage)
            {
                return;
            }

            if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
            {
                return;
            }

            string? message = ResolveMessage(member.Expression);
            if (message is null)
            {
                return; // message not determinable from the chain - do not guess
            }

            string? reason = Vela.Steps.StepImageRules.GetMessageRejectionReason(message, isPreImage);
            if (reason is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), isPreImage ? "A pre-image" : "A post-image", message, reason));
            }
        }

        // Walks the receiver spine looking for the trigger that set the message: a typed OnCreate()/OnDelete()/...
        // or a permissive OnMessage("create"|...). Returns the message, or null if undeterminable.
        private static string? ResolveMessage(ExpressionSyntax? receiver)
        {
            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                if (TypedTriggerMessage.TryGetValue(call.MethodName, out string? typedMessage))
                {
                    return typedMessage;
                }

                if (call.MethodName == "OnMessage" && TryGetLiteralMessage(call.Invocation, out string permissiveMessage))
                {
                    return MessageDisplayNames.TryGetValue(permissiveMessage, out string? displayName) ? displayName : permissiveMessage;
                }
            }

            return null;
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
            return true;
        }
    }
}
