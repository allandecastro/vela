using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags <c>ctx.HasChanged(...)</c> / <c>ctx.HasAnyChanged(...)</c> in a handler whose step registers no
    /// pre-image: these throw at runtime because they diff the Target against a registered pre-image
    /// (StepBuilder / HasChangedCore). To keep false positives at zero this fires only in the unambiguous case:
    /// the handler is an instance method referenced by exactly one <c>RegisterStep()</c> chain in the same class,
    /// that chain has no <c>WithPreImage(...)</c>, and the call uses the default pre-image alias. Lambda handlers,
    /// handlers shared across steps, custom aliases, and anything else are left alone. When that chain's pre-image
    /// lists its columns as selectors, a compared column missing from the list is reported too: its previous value
    /// reads as null, so it looks changed whenever the Target carries it.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HasChangedPreImageAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.HasChangedPreImage;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "HasChanged without a registered pre-image",
            messageFormat: "'{0}' requires a registered pre-image, but the step that runs this handler has no WithPreImage(...); it will throw at runtime",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "HasChanged / HasAnyChanged diff the Target against a registered pre-image and throw when none is registered. Add WithPreImage(...) to the step, or remove the change check.");

        // Same rule, other cause: the step has a pre-image, but its column list omits the compared column, so the
        // pre-image value reads as null and the column looks changed whenever the Target carries it.
        private static readonly DiagnosticDescriptor ColumnRule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "HasChanged without a registered pre-image",
            messageFormat: "'{0}' compares column '{1}', which the step's WithPreImage(...) does not capture; its previous value reads as null, so it looks changed whenever it is set",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "HasChanged / HasAnyChanged compare the Target with the registered pre-image. A column missing from the pre-image's column list compares against null and always looks changed. Add the column to WithPreImage(...).");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule, ColumnRule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol? contextType = start.Compilation.GetTypeByMetadataName("Vela.Context.ILocalPluginContext");
                INamedTypeSymbol? fluent = start.Compilation.GetTypeByMetadataName("Vela.Steps.IFluentStep");
                if (contextType is null || fluent is null)
                {
                    return;
                }

                // Per-type cache of the Handle-chain scan: without it, a type with N HasChanged calls re-scans all its
                // Handle chains N times (quadratic). Computed once per owning type; the value factory is pure so a
                // concurrent double-compute is harmless. (#309 P2)
                var perTypeBindings = new ConcurrentDictionary<TypeDeclarationSyntax, IReadOnlyDictionary<ISymbol, Binding>>();
                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeInvocation(nodeContext, contextType, fluent, perTypeBindings), SyntaxKind.InvocationExpression);
            });
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, INamedTypeSymbol contextType, INamedTypeSymbol fluent, ConcurrentDictionary<TypeDeclarationSyntax, IReadOnlyDictionary<ISymbol, Binding>> perTypeBindings)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                return;
            }

            string name = member.Name.Identifier.ValueText;
            if (name != "HasChanged" && name != "HasAnyChanged")
            {
                return;
            }

            // Confirm it is the Vela change-detection extension over ILocalPluginContext.
            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
                || !IsContextChangeCheck(method, contextType))
            {
                return;
            }

            // A HasChanged with an explicit (non-default) pre-image alias is out of scope - matching aliases to the
            // WithPreImage alias is ambiguous, so skip to avoid false positives. HasAnyChanged always uses default.
            // Called as a static method (PluginContextExtensions.HasChanged<T>(ctx, selector)) the context is the
            // first argument, not an alias.
            int receiverArguments = method.ReducedFrom is null && method.IsExtensionMethod ? 1 : 0;
            if (name == "HasChanged" && invocation.ArgumentList.Arguments.Count - receiverArguments > 1)
            {
                return;
            }

            // The enclosing handler must be a plain method (not a lambda/local function/property) so it can be bound
            // to a RegisterStep chain by name.
            MethodDeclarationSyntax? handler = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            TypeDeclarationSyntax? owningType = handler?.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (handler is null || owningType is null)
            {
                return;
            }

            if (context.SemanticModel.GetDeclaredSymbol(handler, context.CancellationToken) is not IMethodSymbol handlerSymbol)
            {
                return;
            }

            // Find every RegisterStep()...Handle(handler) chain in the same type that binds THIS handler by method
            // group. Only act when exactly one chain owns it (unambiguous) and that chain has no WithPreImage. The
            // scan is cached per owning type (see perTypeBindings) so it runs once, not once per HasChanged call.
            IReadOnlyDictionary<ISymbol, Binding> bindings = perTypeBindings.GetOrAdd(
                owningType, ot => BuildHandlerBindings(ot, context.SemanticModel, fluent, context.CancellationToken));

            if (!bindings.TryGetValue(handlerSymbol, out Binding binding) || binding.Chains != 1)
            {
                return;
            }

            if (!binding.HasPreImage)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), name));
                return;
            }

            // The pre-image lists its columns: each compared column must be one of them.
            if (binding.PreImageColumns is { } captured)
            {
                foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments.Skip(receiverArguments))
                {
                    if (argument.Expression is LambdaExpressionSyntax selector &&
                        FluentStep.SelectedMember(selector) is { } column &&
                        !captured.Contains(column.Name.Identifier.ValueText))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(ColumnRule, argument.GetLocation(), name, column.Name.Identifier.ValueText));
                    }
                }
            }
        }

        // Scans a type once, mapping each method-group handler bound by a RegisterStep()...Handle(handler) chain to the
        // number of chains that own it and whether any of those chains declares a WithPreImage.
        private static IReadOnlyDictionary<ISymbol, Binding> BuildHandlerBindings(
            TypeDeclarationSyntax owningType, SemanticModel model, INamedTypeSymbol fluent, CancellationToken cancellationToken)
        {
            var result = new Dictionary<ISymbol, Binding>(SymbolEqualityComparer.Default);
            foreach (InvocationExpressionSyntax handleCall in owningType.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (handleCall.Expression is not MemberAccessExpressionSyntax handleMember || handleMember.Name.Identifier.ValueText != "Handle")
                {
                    continue;
                }

                ArgumentSyntax? handlerArg = handleCall.ArgumentList.Arguments.FirstOrDefault();
                if (handlerArg is null)
                {
                    continue;
                }

                if (FluentStep.BoundHandler(model, handlerArg.Expression, cancellationToken) is not IMethodSymbol bound
                    || !FluentStep.IsFluentMethod(model, handleCall, fluent, cancellationToken))
                {
                    continue;
                }

                (bool hasPreImage, ImmutableHashSet<string>? columns) = ReadPreImage(handleMember.Expression);
                result[bound] = result.TryGetValue(bound, out Binding existing)
                    ? new Binding(existing.Chains + 1, existing.HasPreImage || hasPreImage, null)
                    : new Binding(1, hasPreImage, columns);
            }

            return result;
        }

        // Whether the chain registers a default pre-image and, when every WithPreImage(...) lists its columns as
        // selectors on the entity, the names of those columns. null columns = unknown or every column (a capture-all
        // WithPreImage(), an alias, a selector the analyzer cannot read), which is never second-guessed.
        private static (bool HasPreImage, ImmutableHashSet<string>? Columns) ReadPreImage(ExpressionSyntax receiver)
        {
            bool hasPreImage = false;
            ImmutableHashSet<string>.Builder columns = ImmutableHashSet.CreateBuilder<string>(System.StringComparer.Ordinal);
            bool known = true;
            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                if (call.MethodName != "WithPreImage")
                {
                    continue;
                }

                hasPreImage = true;
                known &= call.Arguments.Count > 0;
                foreach (ArgumentSyntax argument in call.Arguments)
                {
                    if (argument.Expression is LambdaExpressionSyntax selector && FluentStep.SelectedMember(selector) is { } column)
                    {
                        columns.Add(column.Name.Identifier.ValueText);
                    }
                    else
                    {
                        known = false;
                    }
                }
            }

            return (hasPreImage, hasPreImage && known ? columns.ToImmutable() : null);
        }

        private static bool IsContextChangeCheck(IMethodSymbol method, INamedTypeSymbol contextType)
        {
            ITypeSymbol? receiver = method.ReducedFrom is not null
                ? method.ReceiverType
                : method.Parameters.Length > 0 ? method.Parameters[0].Type : null;

            if (receiver is null)
            {
                return false;
            }

            if (SymbolEqualityComparer.Default.Equals(receiver.OriginalDefinition, contextType))
            {
                return true;
            }

            return receiver.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, contextType));
        }

        private readonly struct Binding
        {
            public Binding(int chains, bool hasPreImage, ImmutableHashSet<string>? preImageColumns)
            {
                Chains = chains;
                HasPreImage = hasPreImage;
                PreImageColumns = preImageColumns;
            }

            public int Chains { get; }
            public bool HasPreImage { get; }
            public ImmutableHashSet<string>? PreImageColumns { get; }
        }
    }
}
