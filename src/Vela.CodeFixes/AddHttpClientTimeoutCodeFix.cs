using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace Vela.CodeFixes
{
    /// <summary>
    /// Adds <c>Timeout = TimeSpan.FromSeconds(30)</c> to the object initializer of a <c>new HttpClient(...)</c>
    /// (creating the initializer when there is none) - the VELA026 fix. 30 seconds is a starting point the author
    /// adjusts to the service; the fix only makes the timeout explicit.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddHttpClientTimeoutCodeFix)), Shared]
    public sealed class AddHttpClientTimeoutCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.HttpClientTimeout);

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
                if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not BaseObjectCreationExpressionSyntax creation ||
                    creation.Initializer is { } existing && !existing.IsKind(SyntaxKind.ObjectInitializerExpression))
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Set HttpClient.Timeout",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(creation, WithTimeout(creation)))),
                        equivalenceKey: "AddHttpClientTimeout"),
                    diagnostic);
            }
        }

        private static BaseObjectCreationExpressionSyntax WithTimeout(BaseObjectCreationExpressionSyntax creation)
        {
            // global::System.TimeSpan, reduced to TimeSpan by the simplifier when 'using System;' is in scope.
            var fromSeconds = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression("global::System.TimeSpan.FromSeconds(30)");
            var member = (MemberAccessExpressionSyntax)fromSeconds.Expression;
            fromSeconds = fromSeconds.WithExpression(member.WithExpression(member.Expression.WithAdditionalAnnotations(Simplifier.Annotation)));
            ExpressionSyntax timeout = SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName("Timeout"),
                fromSeconds);

            if (creation.Initializer is { } initializer)
            {
                // Keep the spacing before the closing brace: the last entry's trailing trivia moves to the new one.
                SyntaxTriviaList trailing = initializer.Expressions.Count > 0 ? initializer.Expressions[initializer.Expressions.Count - 1].GetTrailingTrivia() : SyntaxFactory.TriviaList(SyntaxFactory.Space);
                return creation.WithInitializer(initializer.AddExpressions(timeout.WithTrailingTrivia(trailing)));
            }

            // new HttpClient() -> new HttpClient() { Timeout = ... }; keep the creation's trailing trivia after the brace.
            InitializerExpressionSyntax added = SyntaxFactory.InitializerExpression(
                    SyntaxKind.ObjectInitializerExpression,
                    SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.SingletonSeparatedList(timeout),
                    SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Space))
                .WithTrailingTrivia(creation.GetTrailingTrivia());
            return creation.WithoutTrailingTrivia().WithInitializer(added);
        }
    }
}
