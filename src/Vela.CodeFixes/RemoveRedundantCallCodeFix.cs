using System.Collections.Generic;
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
    /// <summary>
    /// Removes a redundant or invalid call from a step chain: a duplicate single-value modifier (VELA003) or an
    /// image that the message can never provide (VELA007). The call is dropped, leaving the rest of the chain. A
    /// duplicate <c>FilterOn(...)</c> is not simply dropped: its columns are merged into the first
    /// <c>FilterOn(...)</c>, so no filtering attribute is lost.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveRedundantCallCodeFix)), Shared]
    public sealed class RemoveRedundantCallCodeFix : CodeFixProvider
    {
        private const string FilterOn = "FilterOn";

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.DuplicateModifier, DiagnosticIds.ImageStage);

        // A custom fix-all instead of the BatchFixer: with 3+ duplicate modifiers the reported calls are nested
        // invocation nodes, and the BatchFixer's overlapping ReplaceNode edits drop one. This removes every
        // redundant call in a document in a single ReplaceNodes rewrite, which resolves the nesting correctly.
        public override FixAllProvider GetFixAllProvider() => new RemoveAllRedundantCallsFixAllProvider();

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            foreach (Diagnostic diagnostic in context.Diagnostics)
            {
                InvocationExpressionSyntax? call = FluentChain.FindCall(root, diagnostic.Location.SourceSpan);
                if (call == null)
                {
                    continue;
                }

                bool merges = diagnostic.Id == DiagnosticIds.DuplicateModifier && FirstFilterOn(call) is not null;
                context.RegisterCodeFix(
                    CodeAction.Create(
                        merges ? "Merge the columns into the first FilterOn(...)" : "Remove this call",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(Rewrite(root, new[] { call }, diagnostic.Id == DiagnosticIds.DuplicateModifier))),
                        equivalenceKey: "RemoveRedundantCall"),
                    diagnostic);
            }
        }

        // Drops every call in `calls` from its chain. For a duplicate modifier, a dropped FilterOn(...) hands its
        // arguments to the first FilterOn(...) of the same chain (skipping a column that is already listed there).
        private static SyntaxNode Rewrite(SyntaxNode root, IReadOnlyCollection<InvocationExpressionSyntax> calls, bool mergeFilters)
        {
            Dictionary<InvocationExpressionSyntax, List<ArgumentSyntax>> merged = new Dictionary<InvocationExpressionSyntax, List<ArgumentSyntax>>();
            if (mergeFilters)
            {
                // Innermost (earliest in the chain) first, so the arguments keep their source order.
                foreach (InvocationExpressionSyntax call in calls.OrderBy(c => c.ArgumentList.SpanStart))
                {
                    if (FirstFilterOn(call) is { } first)
                    {
                        if (!merged.TryGetValue(first, out List<ArgumentSyntax>? arguments))
                        {
                            arguments = new List<ArgumentSyntax>();
                            merged[first] = arguments;
                        }

                        arguments.AddRange(call.ArgumentList.Arguments);
                    }
                }
            }

            HashSet<InvocationExpressionSyntax> removed = new HashSet<InvocationExpressionSyntax>(calls);

            // ReplaceNodes rewrites descendants first, so for a nested chain the outer call already sees the inner
            // call replaced (or merged into); dropping each call then leaves exactly the surviving chain.
            return root.ReplaceNodes(
                calls.Concat(merged.Keys).Distinct(),
                (original, rewritten) =>
                {
                    if (removed.Contains(original))
                    {
                        return FluentChain.Receiver(rewritten).WithTriviaFrom(rewritten);
                    }

                    InvocationExpressionSyntax target = rewritten;
                    HashSet<string> listed = new HashSet<string>(target.ArgumentList.Arguments.Select(a => a.Expression.ToString()), System.StringComparer.Ordinal);
                    IEnumerable<ArgumentSyntax> extra = merged[original]
                        .Where(a => listed.Add(a.Expression.ToString()))
                        .Select(a => a.WithoutTrivia());
                    return target.WithArgumentList(target.ArgumentList.AddArguments(extra.ToArray()));
                });
        }

        // The first FilterOn(...) on the chain before a duplicate FilterOn(...) call, or null when the call is not a
        // FilterOn or none precedes it.
        private static InvocationExpressionSyntax? FirstFilterOn(InvocationExpressionSyntax call)
        {
            if (((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText != FilterOn)
            {
                return null;
            }

            InvocationExpressionSyntax? first = null;
            for (ExpressionSyntax? current = FluentChain.Receiver(call); current is not null;)
            {
                switch (current)
                {
                    case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation:
                        if (member.Name.Identifier.ValueText == FilterOn)
                        {
                            first = invocation;
                        }

                        current = member.Expression;
                        break;
                    case ParenthesizedExpressionSyntax parenthesized:
                        current = parenthesized.Expression;
                        break;
                    case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                        current = suppressed.Operand;
                        break;
                    default:
                        current = null;
                        break;
                }
            }

            return first;
        }

        private sealed class RemoveAllRedundantCallsFixAllProvider : DocumentBasedFixAllProvider
        {
            protected override async Task<Document?> FixAllAsync(FixAllContext context, Document document, ImmutableArray<Diagnostic> diagnostics)
            {
                SyntaxNode? root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
                if (root == null)
                {
                    return document;
                }

                // Every reported call in this document. Distinct by node identity so a call is not listed twice.
                List<InvocationExpressionSyntax> calls = diagnostics
                    .Select(d => FluentChain.FindCall(root, d.Location.SourceSpan))
                    .OfType<InvocationExpressionSyntax>()
                    .Distinct()
                    .ToList();
                if (calls.Count == 0)
                {
                    return document;
                }

                // The fix-all runs per diagnostic ID; only VELA003 merges FilterOn columns.
                bool mergeFilters = diagnostics.Any(d => d.Id == DiagnosticIds.DuplicateModifier);
                return document.WithSyntaxRoot(Rewrite(root, calls, mergeFilters));
            }
        }
    }
}
