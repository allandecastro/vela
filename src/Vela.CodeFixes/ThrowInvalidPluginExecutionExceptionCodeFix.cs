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
    /// <summary>Changes the thrown exception to <c>InvalidPluginExecutionException</c> (VELA012).</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ThrowInvalidPluginExecutionExceptionCodeFix)), Shared]
    public sealed class ThrowInvalidPluginExecutionExceptionCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.PluginExceptionType);

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
            SyntaxNode root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
            SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            // Keep only arguments that map to a real InvalidPluginExecutionException constructor: (string message)
            // and/or a trailing (Exception inner). Anything else (e.g. ArgumentException's paramName) is dropped so
            // the result always compiles - the ctors used are (), (string) and (string, Exception).
            ArgumentSyntax? messageArg = null;
            ArgumentSyntax? innerArg = null;
            if (model != null)
            {
                foreach (ArgumentSyntax arg in creation.ArgumentList?.Arguments ?? default)
                {
                    ITypeSymbol? type = model.GetTypeInfo(arg.Expression, cancellationToken).Type;
                    if (messageArg == null && type?.SpecialType == SpecialType.System_String)
                    {
                        messageArg = arg;
                    }
                    else if (innerArg == null && IsException(type))
                    {
                        innerArg = arg;
                    }
                }
            }

            ArgumentListSyntax argumentList = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                new[] { messageArg, innerArg }.Where(a => a != null).Select(a => a!)));

            ObjectCreationExpressionSyntax replacement = creation
                .WithType(SyntaxFactory.ParseTypeName("global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException").WithTriviaFrom(creation.Type))
                .WithArgumentList(argumentList)
                .WithInitializer(null);

            return document.WithSyntaxRoot(root.ReplaceNode(creation, replacement));
        }

        private static bool IsException(ITypeSymbol? type)
        {
            for (ITypeSymbol? t = type; t != null; t = t.BaseType)
            {
                if (t.Name == "Exception" && t.ContainingNamespace?.ToDisplayString() == "System")
                {
                    return true;
                }
            }

            return false;
        }
    }
}
