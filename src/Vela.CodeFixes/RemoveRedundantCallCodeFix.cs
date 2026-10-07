using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Vela.CodeFixes
{
    /// <summary>
    /// Removes a redundant or invalid call from a step chain: a duplicate single-value modifier (VELA003) or an
    /// image that the message can never provide (VELA007). The call is dropped, leaving the rest of the chain.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveRedundantCallCodeFix)), Shared]
    public sealed class RemoveRedundantCallCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.DuplicateModifier, DiagnosticIds.ImageStage);

        // A custom fix-all instead of the BatchFixer: with 3+ duplicate modifiers the reported calls are nested
        // invocation nodes, and the BatchFixer's overlapping ReplaceNode edits drop one. This removes every
        // redundant call in a document in a single ReplaceNodes rewrite, which resolves the nesting correctly.
        public override FixAllProvider GetFixAllProvider() => new RemoveAllRedundantCallsFixAllProvider();

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
                if (call == null)
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Remove this call",
                        ct => RemoveAsync(context.Document, call, ct),
                        equivalenceKey: "RemoveRedundantCall"),
                    diagnostic);
            }
        }

        private static async Task<Document> RemoveAsync(Document document, InvocationExpressionSyntax call, System.Threading.CancellationToken cancellationToken)
        {
            DocumentEditor editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
            editor.ReplaceNode(call, FluentChain.Receiver(call).WithTriviaFrom(call));
            return editor.GetChangedDocument();
        }

        private sealed class RemoveAllRedundantCallsFixAllProvider : DocumentBasedFixAllProvider
        {
            protected override async Task<Document?> FixAllAsync(FixAllContext context, Document document, ImmutableArray<Diagnostic> diagnostics)
            {
                SyntaxNode? root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
                if (root == null)
                {
                    return document;
                }

                // Every reported call in this document. Distinct by node identity so a call is not listed twice.
                List<InvocationExpressionSyntax> calls = diagnostics
                    .Select(d => FluentChain.FindCall(root, d.Location.SourceSpan))
                    .OfType<InvocationExpressionSyntax>()
                    .Distinct()
                    .ToList();
                if (calls.Count == 0)
                {
                    return document;
                }

                // ReplaceNodes rewrites descendants first, so for a nested chain the outer call already sees the
                // inner call replaced by its receiver; dropping each call then leaves exactly the surviving chain.
                SyntaxNode newRoot = root.ReplaceNodes(
                    calls,
                    (_, rewritten) => FluentChain.Receiver(rewritten).WithTriviaFrom(rewritten));
                return document.WithSyntaxRoot(newRoot);
            }
        }
    }
}
