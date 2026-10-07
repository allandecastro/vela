using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vela.CodeFixes
{
    /// <summary>Declares a plugin class (or a type enclosing it) <c>partial</c> so the generated nested types fit - the VELA014 fix.</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddPartialModifierCodeFix)), Shared]
    public sealed class AddPartialModifierCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.PartialPluginForTypedImage);

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
                TypeDeclarationSyntax? declaration = root.FindNode(diagnostic.Location.SourceSpan)
                    .FirstAncestorOrSelf<TypeDeclarationSyntax>();
                if (declaration == null || declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    continue;
                }

                TypeDeclarationSyntax target = declaration;
                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Make the plugin class partial",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(target, AddPartial(target)))),
                        equivalenceKey: "AddPartialModifier"),
                    diagnostic);
            }
        }

        private static TypeDeclarationSyntax AddPartial(TypeDeclarationSyntax declaration)
        {
            SyntaxToken partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space);

            if (declaration.Modifiers.Count == 0)
            {
                // No modifiers: the class keyword carries the leading indentation/newline; hand it to partial so the
                // declaration keeps its position instead of collapsing onto the previous line.
                SyntaxToken keyword = declaration.Keyword;
                return declaration
                    .WithKeyword(keyword.WithLeadingTrivia())
                    .WithModifiers(SyntaxFactory.TokenList(partial.WithLeadingTrivia(keyword.LeadingTrivia)));
            }

            // partial must sit immediately before the type keyword ('class', 'struct', 'record'), after any other modifiers.
            return declaration.AddModifiers(partial);
        }
    }
}
