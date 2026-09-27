using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vela.CodeFixes
{
    /// <summary>Makes a static step handler an instance method (removes <c>static</c>) - the VELA004 fix.</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveStaticModifierCodeFix)), Shared]
    public sealed class RemoveStaticModifierCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("VELA004");

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            SemanticModel? model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            foreach (Diagnostic diagnostic in context.Diagnostics)
            {
                SyntaxNode handler = root.FindNode(diagnostic.Location.SourceSpan);
                // The handler is a method name (StaticHandler) or member access (Handlers.OnCreate); the method
                // name is the last simple name. FindNode may return the enclosing argument, so search within it.
                SimpleNameSyntax? nameNode = handler.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().LastOrDefault();
                if (nameNode == null)
                {
                    continue;
                }

                // Prefer the bound method symbol (correct with overloads); fall back to a name match. A method group
                // resolves via CandidateSymbols, not Symbol.
                MethodDeclarationSyntax? declaration = null;
                if (model != null)
                {
                    SymbolInfo info = model.GetSymbolInfo(nameNode, context.CancellationToken);
                    if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IMethodSymbol method)
                    {
                        declaration = method.DeclaringSyntaxReferences
                            .Select(r => r.GetSyntax(context.CancellationToken))
                            .OfType<MethodDeclarationSyntax>()
                            .FirstOrDefault(d => d.Modifiers.Any(SyntaxKind.StaticKeyword));
                    }
                }

                declaration ??= root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(d => d.Identifier.ValueText == nameNode.Identifier.ValueText && d.Modifiers.Any(SyntaxKind.StaticKeyword));
                if (declaration == null)
                {
                    continue;
                }

                MethodDeclarationSyntax target = declaration;
                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Make the handler an instance method",
                        ct => ApplyAsync(context.Document.Project.Solution, target, ct),
                        equivalenceKey: "RemoveStaticModifier"),
                    diagnostic);
            }
        }

        // Solution-based: the handler method may be declared in a DIFFERENT file from the .Handle(...) diagnostic, so
        // edit the document that owns it rather than the context document (else ReplaceNode throws "node not in tree", #309 M3).
        private static async Task<Solution> ApplyAsync(Solution solution, MethodDeclarationSyntax method, CancellationToken cancellationToken)
        {
            DocumentId? documentId = solution.GetDocumentId(method.SyntaxTree);
            if (documentId == null)
            {
                return solution;
            }

            SyntaxNode oldRoot = await method.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            return solution.WithDocumentSyntaxRoot(documentId, oldRoot.ReplaceNode(method, RemoveStatic(method)));
        }

        private static MethodDeclarationSyntax RemoveStatic(MethodDeclarationSyntax declaration)
        {
            SyntaxToken staticToken = declaration.Modifiers.First(m => m.IsKind(SyntaxKind.StaticKeyword));
            List<SyntaxToken> kept = declaration.Modifiers.Where(m => !m.IsKind(SyntaxKind.StaticKeyword)).ToList();

            if (kept.Count == 0)
            {
                // static was the only modifier: hand its leading trivia (the method's indentation/newline) to the
                // return type so the method keeps its position.
                return declaration.WithModifiers(SyntaxFactory.TokenList())
                    .WithReturnType(declaration.ReturnType.WithLeadingTrivia(staticToken.LeadingTrivia));
            }

            // If static was the leading modifier, its leading trivia carried the method's indentation/newline;
            // transfer it to the new first modifier so removing static does not collapse the method onto the
            // previous line.
            if (declaration.Modifiers[0].IsKind(SyntaxKind.StaticKeyword))
            {
                kept[0] = kept[0].WithLeadingTrivia(staticToken.LeadingTrivia);
            }

            return declaration.WithModifiers(SyntaxFactory.TokenList(kept));
        }
    }
}
