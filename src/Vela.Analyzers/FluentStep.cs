using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vela.Analyzers
{
    /// <summary>
    /// Shared recognition of a call on Vela's fluent step builder: an invocation whose method is declared on (or on
    /// a type implementing) <c>Vela.Steps.IFluentStep</c>. Used to scope the step-authoring analyzers so a
    /// same-named method on an unrelated type is ignored.
    /// </summary>
    internal static class FluentStep
    {
        public static bool IsFluentMethod(SemanticModel model, InvocationExpressionSyntax invocation, INamedTypeSymbol fluent, System.Threading.CancellationToken cancellationToken)
        {
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            {
                return false;
            }

            return ImplementsFluent(method.ContainingType, fluent);
        }

        // The method a `.Handle(X)` argument binds to. When the call resolves to the Handle(Delegate) overload (a handler
        // taking a generated typed context, or any context other than the config's Handle(Action<...>) one), Roslyn
        // leaves Symbol null and reports the method only as a candidate; a single candidate is that method.
        public static IMethodSymbol? BoundHandler(SemanticModel model, ExpressionSyntax handler, System.Threading.CancellationToken cancellationToken)
        {
            SymbolInfo info = model.GetSymbolInfo(handler, cancellationToken);
            return info.Symbol as IMethodSymbol
                ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] as IMethodSymbol : null);
        }

        public static bool ImplementsFluent(ITypeSymbol type, INamedTypeSymbol fluent)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, fluent))
            {
                return true;
            }

            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, fluent))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
