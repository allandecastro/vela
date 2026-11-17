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
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.StaticHandler);

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
                if (model == null || HandlerDeclaration(root.FindNode(diagnostic.Location.SourceSpan), model, context.CancellationToken) is not { } target)
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Make the handler an instance method",
                        ct => ApplyAsync(context.Document.Project.Solution, target, ct),
                        equivalenceKey: "RemoveStaticModifier"),
                    diagnostic);
            }
        }

        // The declaration of the static handler to make an instance method, or null when the fix would not compile or
        // would edit the wrong method. Offered only when the handler is named without a qualifier (Handle(OnCreated),
        // not Handle(Handlers.OnCreated)), binds to exactly one method, that method is declared in the class making the
        // call or one of its base classes, and neither that class nor the calling member is static: the call then
        // reaches the instance method through the implicit this. A same-named method elsewhere is never touched.
        private static MethodDeclarationSyntax? HandlerDeclaration(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            ExpressionSyntax? handler = node is ArgumentSyntax argument ? argument.Expression : node as ExpressionSyntax;
            if (handler is not SimpleNameSyntax name)
            {
                return null;
            }

            SymbolInfo info = model.GetSymbolInfo(name, cancellationToken);
            if ((info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) is not IMethodSymbol { IsStatic: true } method)
            {
                return null;
            }

            if (name.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } callingDeclaration
                || model.GetDeclaredSymbol(callingDeclaration, cancellationToken) is not INamedTypeSymbol callingType
                || callingType.IsStatic
                || method.ContainingType.IsStatic
                || !IsSelfOrBase(method.ContainingType, callingType)
                || model.GetEnclosingSymbol(name.SpanStart, cancellationToken) is not { } caller
                || IsStaticContext(caller))
            {
                return null;
            }

            // A partial method's two declarations must agree; leave it to the developer. The bound symbol is the
            // defining part, whose DeclaringSyntaxReferences lists only its own declaration, so ask for the other part.
            if (method.PartialImplementationPart != null || method.PartialDefinitionPart != null)
            {
                return null;
            }

            // A static local function is a method symbol too, but no member declaration of the class.
            return method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as MethodDeclarationSyntax;
        }

        private static bool IsSelfOrBase(INamedTypeSymbol candidate, INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, candidate.OriginalDefinition))
                {
                    return true;
                }
            }

            return false;
        }

        // A static member, a static lambda or local function, or any of them inside one: no implicit this to reach an
        // instance method.
        private static bool IsStaticContext(ISymbol caller)
        {
            for (ISymbol? s = caller; s != null && s is not INamedTypeSymbol; s = s.ContainingSymbol)
            {
                if (s.IsStatic)
                {
                    return true;
                }
            }

            return false;
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
