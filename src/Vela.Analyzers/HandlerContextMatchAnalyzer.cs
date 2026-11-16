using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a step handler bound with <c>.Handle(...)</c> whose parameter is not a valid context for that step's
    /// message and stage. The <c>Handle(Delegate)</c> overload (which lets a method group bind the generated typed
    /// step context via its natural type) is permissive at compile time, so this restores the guarantee the specific
    /// <c>Handle(Action&lt;IXxxContext&gt;)</c> overload used to give: the handler must take the step's message context
    /// (for example <c>IUpdatePostContext&lt;T&gt;</c>), its generated typed context (a subclass of it), or the plain
    /// <c>ILocalPluginContext</c>. A context for a different message or stage (or an unrelated type) is reported.
    ///
    /// Scope: every typed <c>On{Message}()</c> trigger (<c>TypedTriggers.Messages</c>, shared with the runtime), whose
    /// context is <c>I{Message}{Pre|Post}Context&lt;TEntity&gt;</c>. The type-stated <c>On&lt;TRequest&gt;()</c> and
    /// <c>OnMessage&lt;TMessage&gt;()</c> paths are left to the runtime, which checks the handler's context when the
    /// steps are registered: their request type is not always known statically.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HandlerContextMatchAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.HandlerContextMatch;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Step handler context must match the step",
            messageFormat: "Handler '{0}' takes a context that does not match this {1} step; expected '{2}' (or its generated typed context)",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "The handler bound with .Handle(...) must take the step's message context, its generated typed step context (a subclass), or the plain ILocalPluginContext. A context for a different message/stage is a mistake the permissive Handle(Delegate) overload cannot catch on its own.");

        // Same rule, other shape: Handle(Delegate) also accepts a method of any arity or return type, which the
        // builder rejects when the step is registered (StepBuilder.SetHandler). Reported on every trigger path.
        private static readonly DiagnosticDescriptor ShapeRule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Step handler context must match the step",
            messageFormat: "Handler '{0}' must be a void method taking exactly one context parameter; it takes {1} parameter(s) and returns '{2}'",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A step handler bound with .Handle(...) must return void and take exactly one context parameter. The permissive Handle(Delegate) overload compiles any method group, and the step builder then throws when the step is registered.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule, ShapeRule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol? fluent = start.Compilation.GetTypeByMetadataName(FluentStep.FluentStepMetadataName);
                if (fluent is null)
                {
                    return;
                }

                INamedTypeSymbol? plainContext = start.Compilation.GetTypeByMetadataName("Vela.Context.ILocalPluginContext");
                INamedTypeSymbol? entityPluginBase = start.Compilation.GetTypeByMetadataName(PluginTypes.EntityPluginBaseMetadataName);
                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeHandle(nodeContext, fluent, plainContext, entityPluginBase), SyntaxKind.InvocationExpression);
            });
        }

        private static void AnalyzeHandle(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent, INamedTypeSymbol? plainContext, INamedTypeSymbol? entityPluginBase)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            if (!FluentStep.IsHandleCall(invocation, out MemberAccessExpressionSyntax member, out ExpressionSyntax argument))
            {
                return;
            }

            // Only the method-group path is permissive; a lambda binds the specific typed overload and is already
            // checked by the compiler.
            if (argument is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                return;
            }

            if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken) ||
                FluentStep.BoundHandler(context.SemanticModel, argument, context.CancellationToken) is not IMethodSymbol handler)
            {
                return;
            }

            // A method of the wrong shape can only have bound Handle(Delegate); the builder throws on it at
            // registration, whatever the trigger.
            if (handler.Parameters.Length != 1 || !handler.ReturnsVoid)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ShapeRule, argument.GetLocation(), handler.Name, handler.Parameters.Length,
                    handler.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                return;
            }

            // Typed message + stage from the chain preceding .Handle(...).
            (string? message, bool? isPost) = ResolveGuidedMessageAndStage(member.Expression);
            if (message is null || isPost is null)
            {
                return;
            }

            string expectedInterface = "I" + message + (isPost.Value ? "Post" : "Pre") + "Context";
            INamedTypeSymbol? expectedSymbol = context.Compilation.GetTypeByMetadataName($"Vela.Context.{expectedInterface}`1");

            ITypeSymbol parameterType = handler.Parameters[0].Type;

            // The plugin entity is needed both to validate the context's generic argument and to build the code-fix type.
            INamedTypeSymbol? entity = ResolveEntity(invocation, context.SemanticModel, entityPluginBase, context.CancellationToken);

            if (SatisfiesContext(parameterType, expectedInterface, expectedSymbol, plainContext, entity))
            {
                return;
            }

            // Carry the fully-qualified expected message context (with the plugin's entity) so the code fix can set it.
            ImmutableDictionary<string, string?> properties = ImmutableDictionary<string, string?>.Empty;
            if (entity is not null)
            {
                string expectedType = $"global::Vela.Context.{expectedInterface}<{entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>";
                properties = properties.Add("ExpectedType", expectedType);
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Rule, argument.GetLocation(), properties, handler.Name, message + (isPost.Value ? " post-operation" : " pre-operation"), expectedInterface));
        }

        // The TEntity of the enclosing EntityPluginBase<TEntity>, used to build the fully-qualified expected context.
        private static INamedTypeSymbol? ResolveEntity(SyntaxNode node, SemanticModel model, INamedTypeSymbol? entityPluginBase, System.Threading.CancellationToken cancellationToken)
        {
            ClassDeclarationSyntax? pluginClass = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            return pluginClass is null
                ? null
                : PluginTypes.EntityOf(model.GetDeclaredSymbol(pluginClass, cancellationToken), entityPluginBase);
        }

        // The parameter is valid when it is (or derives from / implements) the expected message-context interface for
        // the plugin's own entity, or is the plain ILocalPluginContext (permissive untyped handler). Both are compared
        // by symbol (Vela.Context.ILocalPluginContext, Vela.Context.I{Msg}{Stage}Context`1); the expected interface
        // falls back to its name only when the compilation does not define it.
        private static bool SatisfiesContext(ITypeSymbol parameterType, string expectedInterface, INamedTypeSymbol? expectedSymbol, INamedTypeSymbol? plainContext, INamedTypeSymbol? entity)
        {
            // The plain untyped handler (parameter is exactly ILocalPluginContext) is allowed. Note we do NOT accept a
            // type merely because it implements ILocalPluginContext - every message context does, so that would accept
            // a wrong context too.
            if (plainContext is not null && SymbolEqualityComparer.Default.Equals(parameterType, plainContext))
            {
                return true;
            }

            // Unresolved (error) type: stay silent to avoid piling on top of the compiler's own error.
            if (parameterType.TypeKind == TypeKind.Error)
            {
                return true;
            }

            // Look at the parameter itself and every interface it implements. The message context is the generic
            // IXxxContext<TEntity>; a matching name for a DIFFERENT entity (for example IUpdatePostContext<Contact> on
            // EntityPluginBase<Account>) compiles through Handle(Delegate) but fails the image cast at runtime, so it
            // must be reported rather than accepted on the name alone.
            foreach (INamedTypeSymbol candidate in SelfAndInterfaces(parameterType))
            {
                bool matches = expectedSymbol is not null
                    ? SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, expectedSymbol)
                    : candidate.Name == expectedInterface;
                if (!matches)
                {
                    continue;
                }

                // Entity unknown: keep the pre-existing name-only behavior to avoid false positives.
                if (entity is null || candidate.TypeArguments.Length != 1)
                {
                    return true;
                }

                ITypeSymbol argument = candidate.TypeArguments[0];

                // Unresolved generic argument: stay silent (the compiler already reports it).
                if (argument.TypeKind == TypeKind.Error)
                {
                    return true;
                }

                if (SymbolEqualityComparer.Default.Equals(argument, entity))
                {
                    return true;
                }
            }

            // The expected context shape is absent, or present but bound to the wrong entity: report.
            return false;
        }

        private static System.Collections.Generic.IEnumerable<INamedTypeSymbol> SelfAndInterfaces(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named)
            {
                yield return named;
            }

            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                yield return iface;
            }
        }

        // Typed-chain resolver: the message from a typed On{Message}() trigger (its name, against the shared
        // TypedTriggers list) and whether the stage is PostOperation. Returns (null, null) for anything else
        // (type-stated On<TRequest>(), OnMessage<TMessage>(), or an unresolved chain), so those paths are left alone.
        private static (string? Message, bool? IsPost) ResolveGuidedMessageAndStage(ExpressionSyntax? receiver)
        {
            string? message = null;
            bool? isPost = null;

            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                switch (call.MethodName)
                {
                    case "PostOperation": isPost ??= true; break;
                    case "PreOperation":
                    case "PreValidation": isPost ??= false; break;
                    default:
                        if (call.Name is not GenericNameSyntax && FluentStep.TypedTriggerMessage(call.MethodName) is { } typed)
                        {
                            message ??= typed;
                        }

                        break;
                }
            }

            return (message, isPost);
        }
    }
}
