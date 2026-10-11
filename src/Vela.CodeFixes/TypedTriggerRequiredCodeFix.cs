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
    /// <summary>
    /// Replaces <c>On&lt;UpdateRequest&gt;()</c> (or <c>OnAsyncOnly&lt;UpdateRequest&gt;()</c>) with the message's typed
    /// trigger <c>OnUpdate()</c> (VELA023). The trigger comes from the diagnostic, which read it off the trigger
    /// interface, so there is no list to keep in sync. The handler may then need the typed context of the trigger.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(TypedTriggerRequiredCodeFix)), Shared]
    public sealed class TypedTriggerRequiredCodeFix : CodeFixProvider
    {
        private const string TriggerProperty = "Trigger";

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.TypedTriggerRequired);

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
                if (!diagnostic.Properties.TryGetValue(TriggerProperty, out string? trigger) || string.IsNullOrEmpty(trigger))
                {
                    continue;
                }

                InvocationExpressionSyntax? call = FluentChain.FindCall(root, diagnostic.Location.SourceSpan);
                if (call?.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax })
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        $"Use {trigger}()",
                        ct => ReplaceAsync(context.Document, call, trigger!, ct),
                        equivalenceKey: "TypedTriggerRequired"),
                    diagnostic);
            }
        }

        private static async Task<Document> ReplaceAsync(Document document, InvocationExpressionSyntax call, string trigger, CancellationToken cancellationToken)
        {
            DocumentEditor editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
            MemberAccessExpressionSyntax member = (MemberAccessExpressionSyntax)call.Expression;
            InvocationExpressionSyntax replacement = SyntaxFactory.InvocationExpression(
                    member.WithName(SyntaxFactory.IdentifierName(trigger).WithTriviaFrom(member.Name)),
                    SyntaxFactory.ArgumentList())
                .WithTriviaFrom(call);
            editor.ReplaceNode(call, replacement);
            return editor.GetChangedDocument();
        }
    }
}
