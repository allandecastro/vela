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
    /// <summary>Changes the thrown exception type to <c>InvalidPluginExecutionException</c> (VELA012), keeping the arguments.</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ThrowInvalidPluginExecutionExceptionCodeFix)), Shared]
    public sealed class ThrowInvalidPluginExecutionExceptionCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("VELA012");

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
                ObjectCreationExpressionSyntax? creation = root.FindNode(diagnostic.Location.SourceSpan)
                    .FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
                if (creation == null)
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Throw InvalidPluginExecutionException",
                        ct => ReplaceAsync(context.Document, creation, ct),
                        equivalenceKey: "ThrowInvalidPluginExecutionException"),
                    diagnostic);
            }
        }

        private static async Task<Document> ReplaceAsync(Document document, ObjectCreationExpressionSyntax creation, CancellationToken cancellationToken)
        {
            DocumentEditor editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
            TypeSyntax replacement = SyntaxFactory.ParseTypeName("Microsoft.Xrm.Sdk.InvalidPluginExecutionException")
                .WithTriviaFrom(creation.Type);
            editor.ReplaceNode(creation.Type, replacement);
            return editor.GetChangedDocument();
        }
    }
}
