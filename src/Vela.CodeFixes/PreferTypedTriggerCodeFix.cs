using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Vela.CodeFixes
{
    /// <summary>Replaces the permissive <c>OnMessage("create")</c> with the typed trigger <c>OnCreate()</c> (VELA006).</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PreferTypedTriggerCodeFix)), Shared]
    public sealed class PreferTypedTriggerCodeFix : CodeFixProvider
    {
        // Modelled messages that have a typed On...() trigger, by their lower-cased message name. Must stay in sync
        // with PreferTypedMessageAnalyzer.TypedTriggers so every flagged message gets a fix.
        private static readonly Dictionary<string, string> TypedTriggers = new Dictionary<string, string>
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
        };

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.PreferTypedMessage);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            foreach (Diagnostic diagnostic in context.Diagnostics)
            {
                InvocationExpressionSyntax? call = FluentChain.FindCall(root, diagnostic.Location.SourceSpan);
                if (call == null ||
                    call.ArgumentList.Arguments.Count != 1 ||
                    call.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax literal ||
                    !literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }

                if (!TypedTriggers.TryGetValue(literal.Token.ValueText.Trim().ToLowerInvariant(), out string trigger))
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        $"Use {trigger}()",
                        ct => ReplaceAsync(context.Document, call, trigger, ct),
                        equivalenceKey: "PreferTypedTrigger"),
                    diagnostic);
            }
        }

        private static async Task<Document> ReplaceAsync(Document document, InvocationExpressionSyntax call, string trigger, CancellationToken cancellationToken)
        {
            DocumentEditor editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
            MemberAccessExpressionSyntax member = (MemberAccessExpressionSyntax)call.Expression;
            InvocationExpressionSyntax replacement = SyntaxFactory.InvocationExpression(
                    member.WithName(SyntaxFactory.IdentifierName(trigger)),
                    SyntaxFactory.ArgumentList())
                .WithTriviaFrom(call);
            editor.ReplaceNode(call, replacement);
            return editor.GetChangedDocument();
        }
    }
}
