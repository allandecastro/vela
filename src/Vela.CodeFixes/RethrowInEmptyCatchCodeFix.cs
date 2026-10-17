using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Vela.CodeFixes
{
    /// <summary>
    /// Puts <c>throw;</c> in an empty catch block - the VELA027 fix. A bare rethrow is valid in any catch clause and
    /// keeps the original exception and stack trace. The exception variable the block never used is dropped
    /// (<c>catch (Exception ex) { }</c> becomes <c>catch (Exception) { throw; }</c>), unless the clause has a
    /// <c>when</c> filter (which may read it).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RethrowInEmptyCatchCodeFix)), Shared]
    public sealed class RethrowInEmptyCatchCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.EmptyCatch);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            SourceText text = await context.Document.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
            foreach (Diagnostic diagnostic in context.Diagnostics)
            {
                if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent is not CatchClauseSyntax clause ||
                    clause.Block.Statements.Count != 0)
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Rethrow the exception (throw;)",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(clause, Rethrow(clause, text)))),
                        equivalenceKey: "RethrowInEmptyCatch"),
                    diagnostic);
            }
        }

        private static CatchClauseSyntax Rethrow(CatchClauseSyntax clause, SourceText text)
        {
            CatchClauseSyntax fixedClause = clause.WithBlock(WithThrow(clause.Block, text));

            CatchDeclarationSyntax? declaration = clause.Declaration;
            if (declaration != null && !declaration.Identifier.IsKind(SyntaxKind.None) && clause.Filter == null)
            {
                // The empty block never read the variable; drop it so the fix does not leave an unused local.
                fixedClause = fixedClause.WithDeclaration(declaration
                    .WithType(declaration.Type.WithoutTrailingTrivia())
                    .WithIdentifier(SyntaxFactory.Token(SyntaxKind.None)));
            }

            return fixedClause;
        }

        // Keeps the block's layout: '{ }' becomes '{ throw; }'; a block whose braces sit on separate lines gets the
        // statement on its own line, one indent level (four spaces) deeper than the closing brace.
        private static BlockSyntax WithThrow(BlockSyntax block, SourceText text)
        {
            ThrowStatementSyntax rethrow = SyntaxFactory.ThrowStatement();
            SyntaxTrivia? newline = FirstNewline(block.OpenBraceToken.TrailingTrivia) ?? FirstNewline(block.CloseBraceToken.LeadingTrivia);
            if (newline is not SyntaxTrivia eol)
            {
                return block
                    .WithOpenBraceToken(block.OpenBraceToken.WithTrailingTrivia(SyntaxFactory.Space))
                    .WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(rethrow.WithTrailingTrivia(SyntaxFactory.Space)))
                    .WithCloseBraceToken(block.CloseBraceToken.WithLeadingTrivia());
            }

            string indent = Indentation(text, block.CloseBraceToken.SpanStart);
            return block
                .WithOpenBraceToken(block.OpenBraceToken.WithTrailingTrivia(eol))
                .WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(rethrow
                    .WithLeadingTrivia(SyntaxFactory.Whitespace(indent + "    "))
                    .WithTrailingTrivia(eol)))
                .WithCloseBraceToken(block.CloseBraceToken.WithLeadingTrivia(SyntaxFactory.Whitespace(indent)));
        }

        private static SyntaxTrivia? FirstNewline(SyntaxTriviaList trivia)
        {
            foreach (SyntaxTrivia t in trivia)
            {
                if (t.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    return t;
                }
            }

            return null;
        }

        // The leading whitespace of the line that holds the position.
        private static string Indentation(SourceText text, int position)
        {
            TextLine line = text.Lines.GetLineFromPosition(position);
            int end = line.Start;
            while (end < line.End && (text[end] == ' ' || text[end] == '\t'))
            {
                end++;
            }

            return text.ToString(TextSpan.FromBounds(line.Start, end));
        }
    }
}
