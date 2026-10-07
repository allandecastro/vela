using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Suggests the typed step trigger over the permissive string form for a modelled message: prefer
    /// <c>OnCreate()</c> to <c>OnMessage("create")</c>. The typed method gives compile-time entity applicability and
    /// a typed handler context, so it is the better default. This is advisory (a <b>warning</b>, not an error):
    /// <c>OnMessage("...")</c> stays valid and is the right tool for a message Vela does not model.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PreferTypedMessageAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.PreferTypedMessage;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Prefer the typed step trigger over OnMessage(string)",
            messageFormat: "Prefer {0}() over OnMessage(\"{1}\") for a modelled message - it gives a typed handler context and compile-time entity applicability",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "For a message Vela models there is a typed trigger (OnCreate, OnUpdate, ...) that is safer than the permissive OnMessage(\"...\") string form. OnMessage stays valid for messages Vela does not model, so this is a warning, not an error.");

        // Modelled messages with a canonical typed On<Message>() trigger. Keyed lower-case; value is the method.
        private static readonly ImmutableDictionary<string, string> TypedTriggers = new Dictionary<string, string>
        {
            ["create"] = "OnCreate",
            ["update"] = "OnUpdate",
            ["delete"] = "OnDelete",
            ["createmultiple"] = "OnCreateMultiple",
            ["updatemultiple"] = "OnUpdateMultiple",
            ["retrieve"] = "OnRetrieve",
            ["retrievemultiple"] = "OnRetrieveMultiple",
            ["associate"] = "OnAssociate",
            ["disassociate"] = "OnDisassociate",
            ["assign"] = "OnAssign",
            ["merge"] = "OnMerge",
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

            if (member.Name.Identifier.ValueText != "OnMessage")
            {
                return;
            }

            // Only a single string-literal argument is considered; a variable/const message is left alone.
            SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count != 1 || arguments[0].Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return;
            }

            string message = literal.Token.ValueText;
            if (!TypedTriggers.TryGetValue(message.Trim().ToLowerInvariant(), out string? suggestion))
            {
                return;
            }

            if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), suggestion, message));
        }
    }
}
