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
            SemanticModel? model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null || model == null)
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
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(Rewrite(root, model, new[] { call }, diagnostic.Id == DiagnosticIds.DuplicateModifier))),
                        equivalenceKey: "RemoveRedundantCall"),
                    diagnostic);
            }
        }

        // Drops every call in `calls` from its chain. For a duplicate modifier, a dropped FilterOn(...) hands its
        // arguments to the first FilterOn(...) of the same chain (skipping a column that is already listed there, so
        // the merge never introduces a VELA005 duplicate).
        private static SyntaxNode Rewrite(SyntaxNode root, SemanticModel model, IReadOnlyCollection<InvocationExpressionSyntax> calls, bool mergeFilters)
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
                    HashSet<string> listed = new HashSet<string>(original.ArgumentList.Arguments.Select(a => ColumnKey(a.Expression, model)), System.StringComparer.Ordinal);
                    IEnumerable<ArgumentSyntax> extra = merged[original]
                        .Where(a => listed.Add(ColumnKey(a.Expression, model)))
                        .Select(a => a.WithoutTrivia());
                    return target.WithArgumentList(target.ArgumentList.AddArguments(extra.ToArray()));
                });
        }

        // What a FilterOn argument selects, independent of how it is written: the member a selector lambda reads on its
        // parameter (a => a.Name and x => (object)x.Name are the same column), or the value of a constant string;
        // anything else by its text.
        private static string ColumnKey(ExpressionSyntax argument, SemanticModel model)
        {
            if (argument is LambdaExpressionSyntax lambda && SelectedColumn(lambda) is string column)
            {
                return "column:" + column;
            }

            if (model.GetConstantValue(argument) is { HasValue: true, Value: string text })
            {
                return "string:" + text;
            }

            return "text:" + argument;
        }

        private static string? SelectedColumn(LambdaExpressionSyntax lambda)
        {
            string? parameter = lambda switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
                ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1
                    => parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
                _ => null,
            };

            if (parameter is null || lambda.Body is not ExpressionSyntax body)
            {
                return null;
            }

            body = Unwrap(body);
            return body is MemberAccessExpressionSyntax member &&
                Unwrap(member.Expression) is IdentifierNameSyntax receiver &&
                receiver.Identifier.ValueText == parameter
                    ? member.Name.Identifier.ValueText
                    : null;
        }

        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (true)
            {
                switch (expression)
                {
                    case CastExpressionSyntax cast:
                        expression = cast.Expression;
                        continue;
                    case BinaryExpressionSyntax asExpression when asExpression.IsKind(SyntaxKind.AsExpression):
                        expression = asExpression.Left;
                        continue;
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        continue;
                    default:
                        return expression;
                }
            }
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
                SemanticModel? model = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                if (root == null || model == null)
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
                return document.WithSyntaxRoot(Rewrite(root, model, calls, mergeFilters));
            }
        }
    }
}
