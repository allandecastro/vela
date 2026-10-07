using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Vela.CodeFixes
{
    /// <summary>Removes the duplicate column selector argument flagged by VELA005.</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveDuplicateColumnCodeFix)), Shared]
    public sealed class RemoveDuplicateColumnCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.DuplicateColumn);

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
                ArgumentSyntax? argument = root.FindNode(diagnostic.Location.SourceSpan)
                    .FirstAncestorOrSelf<ArgumentSyntax>();
                if (argument == null)
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Remove the duplicate column",
                        ct => RemoveAsync(context.Document, argument, ct),
                        equivalenceKey: "RemoveDuplicateColumn"),
                    diagnostic);
            }
        }

        private static async Task<Document> RemoveAsync(Document document, ArgumentSyntax argument, CancellationToken cancellationToken)
        {
            DocumentEditor editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
            editor.RemoveNode(argument);
            return editor.GetChangedDocument();
        }
    }
}
