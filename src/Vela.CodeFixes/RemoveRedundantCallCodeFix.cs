using System.Collections.Immutable;
using System.Composition;
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
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("VELA003", "VELA007");

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
    }
}
