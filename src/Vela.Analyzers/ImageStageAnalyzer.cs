using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a pre/post-image that the message can never provide: a <c>WithPreImage(...)</c> on a Create step (there
    /// is no prior row state before a create), or a <c>WithPostImage(...)</c> on a Delete step (the row no longer
    /// exists afterwards). The typed triggers (<c>OnCreate()</c>, ...) already prevent this through the builder's
    /// type-state, so this targets the permissive <c>OnMessage("create"|"delete")</c> path where the image methods
    /// are exposed unconditionally. The runtime throws for the same cases (StepBuilder.EnsureImageAllowed).
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
            description: "A pre-image on Create (no prior row state) or a post-image on Delete (the row is gone) can never be provided. The runtime rejects these; use the correct image for the message.");

        // Message names (lower-case) mapped to the normalized message this analyzer reasons about.
        private static readonly ImmutableDictionary<string, string> MessageAliases = new Dictionary<string, string>
        {
            ["create"] = "Create",
            ["createmultiple"] = "Create",
            ["update"] = "Update",
            ["updatemultiple"] = "Update",
            ["delete"] = "Delete",
        }.ToImmutableDictionary();

        // Typed trigger method names mapped to their message (OnCreate -> Create, ...).
        private static readonly ImmutableDictionary<string, string> TypedTriggerMessage = new Dictionary<string, string>
        {
            ["OnCreate"] = "Create",
            ["OnCreateMultiple"] = "Create",
            ["OnUpdate"] = "Update",
            ["OnUpdateMultiple"] = "Update",
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

            if (isPreImage && message == "Create")
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), "A pre-image", "Create", "there is no prior row state before a create"));
            }
            else if (isPostImage && message == "Delete")
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), "A post-image", "Delete", "the row no longer exists after a delete"));
            }
        }

        // Walks the receiver spine looking for the trigger that set the message: a typed OnCreate()/OnDelete()/...
        // or a permissive OnMessage("create"|...). Returns the normalized message, or null if undeterminable.
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
                    return MessageAliases.TryGetValue(permissiveMessage, out string? normalized) ? normalized : null;
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
