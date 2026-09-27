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
    /// Scope (known limitation, #309 M1): only the guided single Create/Update/Delete path is validated. The typed
    /// <c>On&lt;TRequest&gt;()</c> and permissive <c>OnMessage(...)</c> paths, and the CreateMultiple/UpdateMultiple
    /// variants, are intentionally left alone - their expected context is the generated per-step context
    /// (<c>MessagePre/PostContext&lt;TRequest,TEntity&gt;</c>), which is not derivable from the guided message/stage
    /// resolution used here. A wrong context on those paths compiles through <c>Handle(Delegate)</c> and is caught at
    /// runtime by the image/context cast rather than at compile time. Extending coverage there is tracked separately.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HandlerContextMatchAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA015";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Step handler context must match the step",
            messageFormat: "Handler '{0}' takes a context that does not match this {1} step; expected '{2}' (or its generated typed context)",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "The handler bound with .Handle(...) must take the step's message context, its generated typed step context (a subclass), or the plain ILocalPluginContext. A context for a different message/stage is a mistake the permissive Handle(Delegate) overload cannot catch on its own.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol? fluent = start.Compilation.GetTypeByMetadataName("Vela.Steps.IFluentStep");
                if (fluent is null)
                {
                    return;
                }

                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeHandle(nodeContext, fluent), SyntaxKind.InvocationExpression);
            });
        }

        private static void AnalyzeHandle(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                member.Name.Identifier.ValueText != "Handle" ||
                invocation.ArgumentList.Arguments.Count != 1)
            {
                return;
            }

            ExpressionSyntax argument = invocation.ArgumentList.Arguments[0].Expression;

            // Only the method-group path is permissive; a lambda binds the specific typed overload and is already
            // checked by the compiler.
            if (argument is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                return;
            }

            if (!FluentStep.IsFluentMethod(context.SemanticModel, invocation, fluent, context.CancellationToken))
            {
                return;
            }

            // Guided message + stage from the chain preceding .Handle(...). Only Create/Update/Delete are handled.
            (string? message, bool? isPost) = ResolveGuidedMessageAndStage(member.Expression);
            if (message is null || isPost is null)
            {
                return;
            }

            string expectedInterface = "I" + message + (isPost.Value ? "Post" : "Pre") + "Context";

            // The bound handler method's single parameter type.
            SymbolInfo info = context.SemanticModel.GetSymbolInfo(argument, context.CancellationToken);
            ISymbol? symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is not IMethodSymbol handler || handler.Parameters.Length != 1)
            {
                return;
            }

            ITypeSymbol parameterType = handler.Parameters[0].Type;

            // The plugin entity is needed both to validate the context's generic argument and to build the code-fix type.
            INamedTypeSymbol? entity = ResolveEntity(invocation, context.SemanticModel, context.CancellationToken);

            if (SatisfiesContext(parameterType, expectedInterface, entity))
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
        private static INamedTypeSymbol? ResolveEntity(SyntaxNode node, SemanticModel model, System.Threading.CancellationToken cancellationToken)
        {
            ClassDeclarationSyntax? pluginClass = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (pluginClass is null || model.GetDeclaredSymbol(pluginClass, cancellationToken) is not INamedTypeSymbol pluginSymbol)
            {
                return null;
            }

            for (INamedTypeSymbol? t = pluginSymbol.BaseType; t is not null; t = t.BaseType)
            {
                if (t.Name == "EntityPluginBase" && t.TypeArguments.Length == 1 && t.TypeArguments[0] is INamedTypeSymbol entity)
                {
                    return entity;
                }
            }

            return null;
        }

        // The parameter is valid when it is (or derives from / implements) the expected message-context interface for
        // the plugin's own entity, or is the plain ILocalPluginContext (permissive untyped handler). Name-based to keep
        // it robust across the generated subclass and the plain interface without binding to the framework assembly.
        private static bool SatisfiesContext(ITypeSymbol parameterType, string expectedInterface, INamedTypeSymbol? entity)
        {
            // The plain untyped handler (parameter is exactly ILocalPluginContext) is allowed. Note we do NOT accept a
            // type merely because it implements ILocalPluginContext - every message context does, so that would accept
            // a wrong context too.
            if (parameterType.Name == "ILocalPluginContext")
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
                if (candidate.Name != expectedInterface)
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

        // Minimal guided-chain resolver: the message from OnCreate/OnUpdate/OnDelete and whether the stage is
        // PostOperation. Returns (null, null) for anything else (typed On<TRequest>(), permissive OnMessage(...), or
        // an unresolved chain), so those paths are left alone.
        private static (string? Message, bool? IsPost) ResolveGuidedMessageAndStage(ExpressionSyntax? receiver)
        {
            string? message = null;
            bool? isPost = null;

            for (ExpressionSyntax? current = receiver; current is not null;)
            {
                if (current is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
                {
                    switch (ma.Name.Identifier.ValueText)
                    {
                        case "OnCreate": message ??= "Create"; break;
                        case "OnUpdate": message ??= "Update"; break;
                        case "OnDelete": message ??= "Delete"; break;
                        case "PostOperation": isPost ??= true; break;
                        case "PreOperation":
                        case "PreValidation": isPost ??= false; break;
                    }

                    current = ma.Expression;
                }
                else if (current is MemberAccessExpressionSyntax bare)
                {
                    current = bare.Expression;
                }
                else
                {
                    break;
                }
            }

            return (message, isPost);
        }
    }
}
