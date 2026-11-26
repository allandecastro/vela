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
    /// Flags writing to the Target (<c>ctx.Request.Target</c> / <c>ctx.GetTarget&lt;T&gt;()</c>) inside a handler
    /// bound to an <b>asynchronous</b> step. An async step runs after the main operation has committed, in its own
    /// execution, so mutating the Target there has no effect: it is never written back to the database and no later
    /// step observes it. To keep false positives at zero this fires only in the unambiguous case: the handler is an
    /// instance method bound by exactly one <c>RegisterStep()</c> chain of its type (any part of a partial type, in any
    /// file), and that chain is <c>.Async()</c>. A write through a local that only ever holds the Target
    /// (<c>var t = ctx.Request.Target;</c>, <c>is Account t</c>) is reported, and so is a call that hands the Target to
    /// a non-virtual method of the same type, declared in the same file, that writes into that parameter (one level
    /// deep). A copy of the Target and a handler shared across
    /// steps are left alone.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class AsyncTargetWriteAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.AsyncTargetWrite;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Write to the Target in an asynchronous step",
            messageFormat: "This async step's handler writes to the Target, which has no effect: an async step runs after the operation commits, so the change is neither persisted nor seen by a later step. Call ctx.Service.Update(...) with the change, or move the logic to a synchronous step.",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "An asynchronous step executes after the main operation has committed. Mutating ctx.Request.Target or ctx.GetTarget<T>() is lost: it is not written back to the database and no downstream step observes it.");

        // Every form that mutates its operand: plain and compound assignment (+=, ??=, ...) and increment/decrement
        // (++ / --). All write to the Target the same way a simple assignment does.
        private static readonly SyntaxKind[] WriteKinds =
        {
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxKind.AddAssignmentExpression,
            SyntaxKind.SubtractAssignmentExpression,
            SyntaxKind.MultiplyAssignmentExpression,
            SyntaxKind.DivideAssignmentExpression,
            SyntaxKind.ModuloAssignmentExpression,
            SyntaxKind.AndAssignmentExpression,
            SyntaxKind.OrAssignmentExpression,
            SyntaxKind.ExclusiveOrAssignmentExpression,
            SyntaxKind.LeftShiftAssignmentExpression,
            SyntaxKind.RightShiftAssignmentExpression,
            SyntaxKind.CoalesceAssignmentExpression,
            SyntaxKind.PreIncrementExpression,
            SyntaxKind.PostIncrementExpression,
            SyntaxKind.PreDecrementExpression,
            SyntaxKind.PostDecrementExpression,
        };

        private delegate bool RootPredicate(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken);

        private static readonly RootPredicate NoRoot = (_, _, _) => false;

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

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

                var state = new State(fluent);
                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeWrite(nodeContext, state), WriteKinds);
                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeHelperCall(nodeContext, state), SyntaxKind.InvocationExpression);
            });
        }

        // Per-compilation caches: the handler bindings of each type, the Target aliases of each handler, and which
        // parameters each same-type helper writes into.
        private sealed class State
        {
            public State(INamedTypeSymbol fluent) => Fluent = fluent;

            public INamedTypeSymbol Fluent { get; }

            public ConcurrentDictionary<INamedTypeSymbol, IReadOnlyDictionary<ISymbol, (int Chains, bool IsAsync)>> Bindings { get; }
                = new ConcurrentDictionary<INamedTypeSymbol, IReadOnlyDictionary<ISymbol, (int Chains, bool IsAsync)>>(SymbolEqualityComparer.Default);

            public ConcurrentDictionary<MethodDeclarationSyntax, ISet<ISymbol>> HandlerAliases { get; }
                = new ConcurrentDictionary<MethodDeclarationSyntax, ISet<ISymbol>>();

            public ConcurrentDictionary<MethodDeclarationSyntax, bool[]> HelperWrites { get; }
                = new ConcurrentDictionary<MethodDeclarationSyntax, bool[]>();
        }

        private static void AnalyzeWrite(SyntaxNodeAnalysisContext context, State state)
        {
            ExpressionSyntax? written = WrittenExpression(context.Node);
            ExpressionSyntax? writeBase = WriteBase(written, context.SemanticModel, context.CancellationToken);
            if (written is null || writeBase is null)
            {
                return;
            }

            MethodDeclarationSyntax? handler = AsyncHandler(context, state);
            if (handler is null)
            {
                return;
            }

            ISet<ISymbol> aliases = HandlerAliases(handler, context, state);
            if (IsRooted(writeBase, context.SemanticModel, aliases, IsTargetExpression, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, written.GetLocation()));
            }
        }

        // One level of indirection: Helper(ctx.Request.Target) / Helper(t) where Helper is a non-virtual method of
        // the handler's own type, declared in the same file, that writes into the matching parameter.
        private static void AnalyzeHelperCall(SyntaxNodeAnalysisContext context, State state)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.ArgumentList.Arguments.Count == 0)
            {
                return;
            }

            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol callee
                || callee.MethodKind != MethodKind.Ordinary
                || callee.IsVirtual || callee.IsAbstract || callee.IsOverride || callee.IsExtern)
            {
                return;
            }

            MethodDeclarationSyntax? helper = callee.OriginalDefinition.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax(context.CancellationToken))
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => (m.Body is not null || m.ExpressionBody is not null) && m.SyntaxTree == invocation.SyntaxTree);
            if (helper is null)
            {
                return;
            }

            bool[] writesParameter = state.HelperWrites.GetOrAdd(
                helper, h => HelperParameterWrites(h, context.SemanticModel, context.CancellationToken));
            if (!writesParameter.Any(writes => writes))
            {
                return;
            }

            MethodDeclarationSyntax? handler = AsyncHandler(context, state);
            if (handler is null
                || context.SemanticModel.GetDeclaredSymbol(handler, context.CancellationToken) is not IMethodSymbol handlerSymbol
                || !SymbolEqualityComparer.Default.Equals(callee.ContainingType, handlerSymbol.ContainingType))
            {
                return;
            }

            ISet<ISymbol> aliases = HandlerAliases(handler, context, state);

            SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;
            for (int i = 0; i < arguments.Count; i++)
            {
                int ordinal = ParameterOrdinal(callee, arguments[i], i);
                if (ordinal < 0 || ordinal >= writesParameter.Length || callee.Parameters[ordinal].IsParams || !writesParameter[ordinal])
                {
                    continue;
                }

                if (IsRooted(arguments[i].Expression, context.SemanticModel, aliases, IsTargetExpression, context.CancellationToken))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
                    return;
                }
            }
        }

        private static int ParameterOrdinal(IMethodSymbol callee, ArgumentSyntax argument, int position)
        {
            if (argument.NameColon is null)
            {
                return position;
            }

            string name = argument.NameColon.Name.Identifier.ValueText;
            for (int i = 0; i < callee.Parameters.Length; i++)
            {
                if (callee.Parameters[i].Name == name)
                {
                    return i;
                }
            }

            return -1;
        }

        private static ISet<ISymbol> HandlerAliases(MethodDeclarationSyntax handler, SyntaxNodeAnalysisContext context, State state)
            => state.HandlerAliases.GetOrAdd(
                handler, h => Aliases(h, context.SemanticModel, IsTargetExpression, seed: null, context.CancellationToken));

        // The enclosing handler when it is bound by exactly one RegisterStep() chain and that chain is .Async().
        private static MethodDeclarationSyntax? AsyncHandler(SyntaxNodeAnalysisContext context, State state)
        {
            MethodDeclarationSyntax? handler = context.Node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (handler is null
                || context.SemanticModel.GetDeclaredSymbol(handler, context.CancellationToken) is not IMethodSymbol handlerSymbol
                || handlerSymbol.ContainingType is not INamedTypeSymbol owningType)
            {
                return null;
            }

            IReadOnlyDictionary<ISymbol, (int Chains, bool IsAsync)> bindings = state.Bindings.GetOrAdd(
                owningType, ot => BuildHandlerBindings(ot, context.SemanticModel, state.Fluent, context.CancellationToken));

            return bindings.TryGetValue(handlerSymbol, out (int Chains, bool IsAsync) binding) && binding.Chains == 1 && binding.IsAsync
                ? handler
                : null;
        }

        // The written expression: the left of an assignment, or the operand of ++/--.
        private static ExpressionSyntax? WrittenExpression(SyntaxNode node) => node switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left,
            PrefixUnaryExpressionSyntax prefix => prefix.Operand,
            PostfixUnaryExpressionSyntax postfix => postfix.Operand,
            _ => null,
        };

        // The entity a write goes INTO: the receiver of a property or indexer write, or the owner of the Attributes
        // collection an entry is written to (t.Attributes["x"] = ...). Re-assigning a reference itself is not a write.
        private static ExpressionSyntax? WriteBase(ExpressionSyntax? written, SemanticModel model, CancellationToken cancellationToken)
        {
            switch (written)
            {
                case ElementAccessExpressionSyntax elementAccess:
                    return Unwrap(elementAccess.Expression, model, cancellationToken) is MemberAccessExpressionSyntax attributes
                        && model.GetSymbolInfo(attributes, cancellationToken).Symbol is IPropertySymbol { Name: "Attributes" } property
                        && property.ContainingType?.ToDisplayString() == "Microsoft.Xrm.Sdk.Entity"
                        ? attributes.Expression
                        : elementAccess.Expression;
                case MemberAccessExpressionSyntax memberAccess:
                    return memberAccess.Expression;
                default:
                    return null;
            }
        }

        // Strips the forms that keep the same object: parentheses, the null-forgiving '!', a reference cast and 'as'.
        // A cast through a user-defined conversion may yield another object, so it is kept (and so never matched).
        private static ExpressionSyntax Unwrap(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
        {
            ExpressionSyntax current = expression;
            while (true)
            {
                switch (current)
                {
                    case ParenthesizedExpressionSyntax paren:
                        current = paren.Expression;
                        break;
                    case PostfixUnaryExpressionSyntax bang when bang.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                        current = bang.Operand;
                        break;
                    case CastExpressionSyntax cast when IsReferenceCast(cast, model, cancellationToken):
                        current = cast.Expression;
                        break;
                    case BinaryExpressionSyntax asCast when asCast.IsKind(SyntaxKind.AsExpression):
                        current = asCast.Left;
                        break;
                    default:
                        return current;
                }
            }
        }

        private static bool IsReferenceCast(CastExpressionSyntax cast, SemanticModel model, CancellationToken cancellationToken)
            => model.GetSymbolInfo(cast, cancellationToken).Symbol is null
               && model.GetTypeInfo(cast.Expression, cancellationToken).Type is { IsReferenceType: true }
               && model.GetTypeInfo(cast, cancellationToken).Type is { IsReferenceType: true };

        // True when the expression is a root itself or a tracked alias of one.
        private static bool IsRooted(ExpressionSyntax expression, SemanticModel model, ISet<ISymbol> aliases, RootPredicate isRoot, CancellationToken cancellationToken)
        {
            // An implicit user-defined conversion (Snapshot s = ctx.Request.Target;, or such an argument) may build
            // another object, so the converted value is not the Target.
            if (model.GetConversion(expression, cancellationToken).IsUserDefined)
            {
                return false;
            }

            ExpressionSyntax current = Unwrap(expression, model, cancellationToken);
            if (current is IdentifierNameSyntax
                && model.GetSymbolInfo(current, cancellationToken).Symbol is ISymbol symbol
                && aliases.Contains(symbol))
            {
                return true;
            }

            return isRoot(current, model, cancellationToken);
        }

        // The locals of a method that only ever hold a root (the Target, or a helper's parameter): declared from one
        // ('var t = ctx.Request.Target;'), bound by a type pattern on one ('ctx.Request.Target is Account t'), and
        // re-assigned only from roots and other such aliases. A local that is ever given anything else (a copy such as
        // 'new Account { Id = t.Id }' or 'ToEntity<T>()', any method result, null), that is compound-assigned,
        // deconstructed into, or passed by ref/out, or that is declared any other way (out var, foreach, switch
        // pattern) is not an alias. The analysis is flow-insensitive, so it only keeps locals that can never hold
        // anything but the Target.
        private static ISet<ISymbol> Aliases(MethodDeclarationSyntax method, SemanticModel model, RootPredicate isRoot, ISymbol? seed, CancellationToken cancellationToken)
        {
            var sources = new Dictionary<ISymbol, List<ExpressionSyntax>>(SymbolEqualityComparer.Default);
            var declared = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var poisoned = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            foreach (SyntaxNode node in method.DescendantNodes())
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax declarator
                        when model.GetDeclaredSymbol(declarator, cancellationToken) is ILocalSymbol { RefKind: RefKind.None } local:
                        declared.Add(local);
                        List<ExpressionSyntax> fromDeclaration = SourcesOf(sources, local);
                        if (declarator.Initializer is not null)
                        {
                            fromDeclaration.Add(declarator.Initializer.Value);
                        }

                        break;
                    case SingleVariableDesignationSyntax designation
                        when designation.Parent is DeclarationPatternSyntax or RecursivePatternSyntax
                            && designation.Parent.Parent is IsPatternExpressionSyntax isPattern
                            && model.GetDeclaredSymbol(designation, cancellationToken) is ILocalSymbol patternLocal:
                        declared.Add(patternLocal);
                        SourcesOf(sources, patternLocal).Add(isPattern.Expression);
                        break;
                    case AssignmentExpressionSyntax assignment:
                        if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                            && assignment.Left is IdentifierNameSyntax assigned
                            && model.GetSymbolInfo(assigned, cancellationToken).Symbol is ILocalSymbol assignedLocal)
                        {
                            SourcesOf(sources, assignedLocal).Add(assignment.Right);
                        }
                        else if (!(assignment.Left is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax))
                        {
                            Poison(assignment.Left, model, poisoned, cancellationToken);
                        }

                        break;
                    case ArgumentSyntax byRef when byRef.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || byRef.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
                        Poison(byRef.Expression, model, poisoned, cancellationToken);
                        break;
                    case RefExpressionSyntax refExpression:
                        Poison(refExpression.Expression, model, poisoned, cancellationToken);
                        break;
                    // t++ / --t on the local itself: an overloaded operator may hand back another instance.
                    // (t.Count++ increments a member, not the local, and keeps the alias.)
                    case PrefixUnaryExpressionSyntax { Operand: IdentifierNameSyntax } prefix
                        when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                        Poison(prefix.Operand, model, poisoned, cancellationToken);
                        break;
                    case PostfixUnaryExpressionSyntax { Operand: IdentifierNameSyntax } postfix
                        when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                        Poison(postfix.Operand, model, poisoned, cancellationToken);
                        break;
                }
            }

            // Least fixed point: a local becomes an alias once every value it is ever given is a root or an alias.
            var aliases = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (seed is not null && !poisoned.Contains(seed) && !sources.ContainsKey(seed))
            {
                aliases.Add(seed);
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (KeyValuePair<ISymbol, List<ExpressionSyntax>> entry in sources)
                {
                    if (aliases.Contains(entry.Key) || !declared.Contains(entry.Key) || poisoned.Contains(entry.Key) || entry.Value.Count == 0)
                    {
                        continue;
                    }

                    if (entry.Value.All(source => IsRooted(source, model, aliases, isRoot, cancellationToken)))
                    {
                        aliases.Add(entry.Key);
                        changed = true;
                    }
                }
            }

            return aliases;
        }

        private static List<ExpressionSyntax> SourcesOf(Dictionary<ISymbol, List<ExpressionSyntax>> sources, ISymbol symbol)
        {
            if (!sources.TryGetValue(symbol, out List<ExpressionSyntax>? list))
            {
                list = new List<ExpressionSyntax>();
                sources[symbol] = list;
            }

            return list;
        }

        // Marks every local or parameter named in a written position (a tuple deconstruction, a ref/out argument).
        private static void Poison(SyntaxNode node, SemanticModel model, HashSet<ISymbol> poisoned, CancellationToken cancellationToken)
        {
            foreach (IdentifierNameSyntax identifier in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is ISymbol symbol and (ILocalSymbol or IParameterSymbol))
                {
                    poisoned.Add(symbol);
                }
            }
        }

        // For each parameter of a helper: whether its body writes into the entity it receives, directly or through a
        // local alias. A by-reference parameter, or one the body re-assigns, is never counted (Aliases drops a seed
        // that is ever assigned), and calls the helper itself makes are not followed (one level deep).
        private static bool[] HelperParameterWrites(MethodDeclarationSyntax helper, SemanticModel model, CancellationToken cancellationToken)
        {
            if (model.GetDeclaredSymbol(helper, cancellationToken) is not IMethodSymbol symbol)
            {
                return System.Array.Empty<bool>();
            }

            ExpressionSyntax[] writeBases = helper.DescendantNodes()
                .Where(node => WriteKinds.Any(kind => node.IsKind(kind)))
                .Select(node => WriteBase(WrittenExpression(node), model, cancellationToken))
                .OfType<ExpressionSyntax>()
                .ToArray();

            var result = new bool[symbol.Parameters.Length];
            for (int i = 0; i < symbol.Parameters.Length && writeBases.Length > 0; i++)
            {
                IParameterSymbol parameter = symbol.Parameters[i];
                if (parameter.RefKind != RefKind.None)
                {
                    continue;
                }

                ISet<ISymbol> aliases = Aliases(helper, model, NoRoot, parameter, cancellationToken);
                result[i] = aliases.Contains(parameter)
                    && writeBases.Any(writeBase => IsRooted(writeBase, model, aliases, NoRoot, cancellationToken));
            }

            return result;
        }

        // True when the expression is the step Target: ctx.GetTarget<T>() (an extension/method named GetTarget) or a
        // request's Target property - both in a Vela namespace, so unrelated .Target members are not matched.
        private static bool IsTargetExpression(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
        {
            ExpressionSyntax current = Unwrap(expression, model, cancellationToken);
            return model.GetSymbolInfo(current, cancellationToken).Symbol switch
            {
                IMethodSymbol method => method.Name == "GetTarget" && InVela(method.ContainingNamespace),
                IPropertySymbol property => property.Name == "Target" && InVela(property.ContainingType?.ContainingNamespace),
                _ => false,
            };
        }

        private static bool InVela(INamespaceSymbol? ns)
            => ns is not null && ns.ToDisplayString().StartsWith("Vela", System.StringComparison.Ordinal);

        // Scans a type once (every part of a partial type), mapping each method-group handler bound by a
        // RegisterStep()...Handle(handler) chain to the number of chains that own it and whether any of those chains
        // is .Async().
        private static IReadOnlyDictionary<ISymbol, (int Chains, bool IsAsync)> BuildHandlerBindings(
            INamedTypeSymbol owningType, SemanticModel currentModel, INamedTypeSymbol fluent, CancellationToken cancellationToken)
        {
            var result = new Dictionary<ISymbol, (int Chains, bool IsAsync)>(SymbolEqualityComparer.Default);
            foreach ((TypeDeclarationSyntax declaration, SemanticModel model) in FluentStep.TypeDeclarations(owningType, currentModel, cancellationToken))
            foreach (InvocationExpressionSyntax handleCall in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
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

                bool isAsync = FluentStep.SpineHasCall(handleMember.Expression, "Async");
                (int Chains, bool IsAsync) existing = result.TryGetValue(bound, out (int Chains, bool IsAsync) e) ? e : (0, false);
                result[bound] = (existing.Chains + 1, existing.IsAsync || isAsync);
            }

            return result;
        }
    }
}
