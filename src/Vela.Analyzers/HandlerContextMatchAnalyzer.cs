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
    /// Scoped to the guided Create/Update/Delete path; the typed/permissive paths are left alone.
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

            if (SatisfiesContext(parameterType, expectedInterface))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Rule, argument.GetLocation(), handler.Name, message + (isPost.Value ? " post-operation" : " pre-operation"), expectedInterface));
        }

        // The parameter is valid when it is (or derives from / implements) the expected message-context interface, or
        // is the plain ILocalPluginContext (permissive untyped handler). Name-based to keep it robust across the
        // generated subclass and the plain interface without binding to the framework assembly.
        private static bool SatisfiesContext(ITypeSymbol parameterType, string expectedInterface)
        {
            // The plain untyped handler (parameter is exactly ILocalPluginContext) is allowed. Note we do NOT accept a
            // type merely because it implements ILocalPluginContext - every message context does, so that would accept
            // a wrong context too.
            if (parameterType.Name == "ILocalPluginContext" || parameterType.Name == expectedInterface)
            {
                return true;
            }

            foreach (INamedTypeSymbol iface in parameterType.AllInterfaces)
            {
                if (iface.Name == expectedInterface)
                {
                    return true;
                }
            }

            // Unresolved (error) type: stay silent to avoid piling on top of the compiler's own error.
            return parameterType.TypeKind == TypeKind.Error;
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
