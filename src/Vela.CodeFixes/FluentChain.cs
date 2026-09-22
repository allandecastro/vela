using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Vela.CodeFixes
{
    /// <summary>Shared helpers for editing a fluent <c>RegisterStep()....</c> chain.</summary>
    internal static class FluentChain
    {
        /// <summary>
        /// The <c>receiver.Method(args)</c> invocation whose member name covers <paramref name="span"/> (the
        /// location the analyzer reports on the method-name identifier), or null.
        /// </summary>
        public static InvocationExpressionSyntax? FindCall(SyntaxNode root, TextSpan span)
        {
            SyntaxNode node = root.FindNode(span);
            for (SyntaxNode? current = node; current is not null; current = current.Parent)
            {
                if (current is InvocationExpressionSyntax invocation && invocation.Expression is MemberAccessExpressionSyntax)
                {
                    return invocation;
                }
            }

            return null;
        }

        /// <summary>The receiver of a <c>receiver.Method(args)</c> call (what remains once the call is dropped).</summary>
        public static ExpressionSyntax Receiver(InvocationExpressionSyntax call) =>
            ((MemberAccessExpressionSyntax)call.Expression).Expression;
    }
}
