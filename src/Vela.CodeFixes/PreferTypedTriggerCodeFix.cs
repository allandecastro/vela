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
        // The typed trigger comes from the diagnostic (read off the trigger interface by the analyzer): no list here.
        private const string TriggerProperty = "Trigger";

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

                if (!diagnostic.Properties.TryGetValue(TriggerProperty, out string? trigger) || string.IsNullOrEmpty(trigger))
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        $"Use {trigger}()",
                        ct => ReplaceAsync(context.Document, call, trigger!, ct),
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
