using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags an empty <c>catch</c> block inside a plugin / Custom API type (lambdas and local functions included): a
    /// block with no statement and nothing else between its braces, whatever it catches and with or without a
    /// <c>when</c> filter. The exception is swallowed and the failure disappears; and when the swallowed exception is a
    /// failed organization-service write inside the pipeline transaction, Dataverse fails the operation anyway ("ISV
    /// code reduced the open transaction count"). A block holding a comment (or a preprocessor directive) is taken as
    /// a deliberate, documented swallow and is not flagged; any statement (<c>throw;</c>, a trace call,
    /// <c>return</c>) is fine too. Syntax only: no semantic guess about what the block should do.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EmptyCatchAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.EmptyCatch;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Empty catch in a plugin",
            messageFormat: "This empty catch swallows the exception, so the failure disappears silently; rethrow it, trace it, or comment why it is ignored",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "An empty catch block in a plugin hides the failure: nothing is traced and the caller never knows. When the swallowed exception is a failed organization-service write inside the pipeline transaction, Dataverse fails the operation anyway with 'ISV code reduced the open transaction count'. Rethrow (throw;), throw an InvalidPluginExecutionException, trace the error, or put a comment in the block to document a deliberate swallow.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                PluginTypes? plugins = PluginTypes.From(start.Compilation);
                if (plugins is null)
                {
                    return; // neither the Dataverse SDK nor Vela: no plugin type to analyze
                }

                start.RegisterSyntaxNodeAction(c => AnalyzeCatch(c, plugins), SyntaxKind.CatchClause);
            });
        }

        private static void AnalyzeCatch(SyntaxNodeAnalysisContext context, PluginTypes plugins)
        {
            var clause = (CatchClauseSyntax)context.Node;
            if (!IsEmpty(clause.Block) || !plugins.Encloses(clause, context.SemanticModel, context.CancellationToken))
            {
                return;
            }

            // Underline the head of the clause (catch, its declaration and filter), not the braces.
            SyntaxNode? last = (SyntaxNode?)clause.Filter ?? clause.Declaration;
            int end = last?.Span.End ?? clause.CatchKeyword.Span.End;
            TextSpan head = TextSpan.FromBounds(clause.CatchKeyword.SpanStart, end);
            context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(clause.SyntaxTree, head)));
        }

        /// <summary>
        /// True when the block has no statement and only whitespace between its braces. With no statement, the braces
        /// are the block's only tokens, so a comment or directive inside it is trivia of one of them.
        /// </summary>
        internal static bool IsEmpty(BlockSyntax block)
            => block.Statements.Count == 0 &&
               OnlyWhitespace(block.OpenBraceToken.TrailingTrivia) &&
               OnlyWhitespace(block.CloseBraceToken.LeadingTrivia);

        private static bool OnlyWhitespace(SyntaxTriviaList trivia)
        {
            foreach (SyntaxTrivia t in trivia)
            {
                if (!t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
