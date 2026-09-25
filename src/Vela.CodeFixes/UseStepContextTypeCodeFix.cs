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
    /// <summary>Sets a step handler's parameter to the step's message context - the VELA015 fix.</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseStepContextTypeCodeFix)), Shared]
    public sealed class UseStepContextTypeCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("VELA015");

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            SemanticModel? model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null || model == null)
            {
                return;
            }

            foreach (Diagnostic diagnostic in context.Diagnostics)
            {
                if (!diagnostic.Properties.TryGetValue("ExpectedType", out string? expectedType) || string.IsNullOrEmpty(expectedType))
                {
                    continue;
                }

                // The diagnostic sits on the .Handle(...) argument (a method group); resolve the bound handler method
                // and rewrite its single parameter's type.
                SimpleNameSyntax? nameNode = root.FindNode(diagnostic.Location.SourceSpan)
                    .DescendantNodesAndSelf().OfType<SimpleNameSyntax>().LastOrDefault();
                if (nameNode == null)
                {
                    continue;
                }

                SymbolInfo info = model.GetSymbolInfo(nameNode, context.CancellationToken);
                if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol methodSymbol)
                {
                    continue;
                }

                MethodDeclarationSyntax? declaration = methodSymbol.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax(context.CancellationToken))
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(d => d.ParameterList.Parameters.Count == 1);
                if (declaration?.ParameterList.Parameters[0].Type == null)
                {
                    continue;
                }

                MethodDeclarationSyntax target = declaration;
                string typeText = expectedType!;
                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Use the step's context type",
                        ct => ApplyAsync(context.Document, target, typeText, ct),
                        equivalenceKey: "UseStepContextType"),
                    diagnostic);
            }
        }

        private static async Task<Document> ApplyAsync(Document document, MethodDeclarationSyntax method, string expectedType, CancellationToken cancellationToken)
        {
            SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return document;
            }

            ParameterSyntax parameter = method.ParameterList.Parameters[0];
            TypeSyntax newType = SyntaxFactory.ParseTypeName(expectedType).WithTriviaFrom(parameter.Type!);
            return document.WithSyntaxRoot(root.ReplaceNode(parameter.Type!, newType));
        }
    }
}
