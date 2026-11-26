using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vela.Analyzers
{
    /// <summary>
    /// Shared recognition of a call on Vela's fluent step builder: an invocation whose method is declared on (or on
    /// a type implementing) <c>Vela.Steps.IFluentStep</c>. Used to scope the step-authoring analyzers so a
    /// same-named method on an unrelated type is ignored. Also hosts the one walk of a fluent chain's receiver
    /// spine (<see cref="Spine"/>) that every analyzer and the generator read a step's trigger, stage and modifiers
    /// through, so they all see the same chain shapes (parentheses, <c>!</c>, property hops).
    /// </summary>
    internal static class FluentStep
    {
        public const string FluentStepMetadataName = "Vela.Steps.IFluentStep";

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

        // Every declaration of a type (each part of a partial type, whatever file it sits in), paired with a semantic
        // model of its tree: the caller's own model for its tree, the compilation's for any other. A plugin split
        // across files registers a step in one part and declares its handler in another, so the handler bindings of
        // a type are read across all of them. The callers cache the result per type and compilation, so a model for
        // another part's tree is asked for once per type. Compilation.GetSemanticModel inside an analyzer is what
        // RS1030 warns about (a fresh model per call is costly); it is deliberate here: a partial type's other parts
        // have no model of their own in this callback, and the per-type cache keeps it to one model per other part.
        public static IEnumerable<(TypeDeclarationSyntax Declaration, SemanticModel Model)> TypeDeclarations(
            INamedTypeSymbol type, SemanticModel current, System.Threading.CancellationToken cancellationToken)
        {
            foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax declaration)
                {
                    continue;
                }

                SemanticModel model = declaration.SyntaxTree == current.SyntaxTree
                    ? current
                    : current.Compilation.GetSemanticModel(declaration.SyntaxTree);
                yield return (declaration, model);
            }
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

        /// <summary>
        /// Every call on the receiver spine of a fluent chain, nearest first: for the receiver
        /// <c>RegisterStep().OnUpdate().PostOperation()</c> it yields <c>PostOperation()</c>, <c>OnUpdate()</c>, then
        /// <c>RegisterStep()</c>. It sees through parentheses and the null-forgiving <c>!</c>, steps over a property
        /// hop (<c>x.Steps.Method()</c>), and stops at the chain root (an unqualified call such as
        /// <c>RegisterStep()</c>, which is yielded too, or anything that is not a call). Argument lambdas are never
        /// entered.
        /// </summary>
        public static IEnumerable<ChainCall> Spine(ExpressionSyntax? receiver)
        {
            ExpressionSyntax? current = receiver;
            while (current is not null)
            {
                switch (current)
                {
                    case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation:
                        yield return new ChainCall(invocation, member.Name);
                        current = member.Expression;
                        break;
                    case InvocationExpressionSyntax { Expression: SimpleNameSyntax name } invocation:
                        yield return new ChainCall(invocation, name);
                        yield break;
                    case MemberAccessExpressionSyntax property:
                        current = property.Expression;
                        break;
                    case ParenthesizedExpressionSyntax parenthesized:
                        current = parenthesized.Expression;
                        break;
                    case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                        current = suppressed.Operand;
                        break;
                    default:
                        yield break;
                }
            }
        }

        /// <summary>
        /// The typed trigger of <paramref name="message"/> on the step trigger type: its <c>On{Message}()</c> member
        /// (parameterless, not generic, matched case-insensitively), or null when the message has none. The trigger
        /// interface is generated from the message model, so this reads the model without a second list.
        /// </summary>
        public static string? FindTypedTrigger(ITypeSymbol triggerType, string message)
        {
            string wanted = "On" + message.Trim();
            foreach (ITypeSymbol type in SelfAndInterfaces(triggerType))
            {
                foreach (ISymbol member in type.GetMembers())
                {
                    if (member is IMethodSymbol { Parameters.Length: 0, TypeParameters.Length: 0, IsStatic: false } candidate
                        && candidate.Name != "OnMessage"
                        && string.Equals(candidate.Name, wanted, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate.Name;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The request name of a typed trigger call, read from its method name (<c>OnAssign</c> -> <c>Assign</c>,
        /// <c>OnSendEmail</c> -> <c>SendEmail</c>), or null when the name is not one of the generated
        /// <c>On{Message}()</c> triggers (<c>TypedTriggers.Messages</c>, shared with the runtime).
        /// </summary>
        public static string? TypedTriggerMessage(string methodName)
        {
            if (methodName.Length <= 2 || !methodName.StartsWith("On", System.StringComparison.Ordinal))
            {
                return null;
            }

            string message = methodName.Substring(2);
            return System.Array.IndexOf(Vela.Steps.TypedTriggers.Messages, message) >= 0 ? message : null;
        }

        private static IEnumerable<ITypeSymbol> SelfAndInterfaces(ITypeSymbol type)
        {
            yield return type;
            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                yield return iface;
            }
        }

        /// <summary>
        /// The message an SDK or Vela request type stands for (<c>Microsoft.Xrm.Sdk.Messages.UpdateRequest</c> -> Update,
        /// <c>Vela.Messages.CreateRequest&lt;T&gt;</c> -> Create), or null for any other type (a custom API request).
        /// </summary>
        public static string? SdkRequestMessage(ITypeSymbol request)
        {
            string ns = request.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (ns != "Microsoft.Xrm.Sdk.Messages" && ns != "Microsoft.Crm.Sdk.Messages" && ns != "Vela.Messages")
            {
                return null;
            }

            const string suffix = "Request";
            string name = request.Name;
            return name.Length > suffix.Length && name.EndsWith(suffix, System.StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : null;
        }

        /// <summary>True when a call named <paramref name="methodName"/> sits on the receiver spine.</summary>
        public static bool SpineHasCall(ExpressionSyntax? receiver, string methodName)
        {
            foreach (ChainCall call in Spine(receiver))
            {
                if (call.MethodName == methodName)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Recognises the shape <c>receiver.Handle(handler)</c> (one argument), the terminal call of a step chain.
        /// The caller still checks the symbol (<see cref="IsFluentMethod"/>) before trusting it.
        /// </summary>
        public static bool IsHandleCall(InvocationExpressionSyntax invocation, out MemberAccessExpressionSyntax member, out ExpressionSyntax handler)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax access &&
                access.Name.Identifier.ValueText == "Handle" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                member = access;
                handler = invocation.ArgumentList.Arguments[0].Expression;
                return true;
            }

            member = null!;
            handler = null!;
            return false;
        }

        /// <summary>
        /// The method name of a method-group handler (<c>OnUpdate</c> or <c>this.OnUpdate</c>), or null for anything
        /// else (a lambda, an anonymous method, a delegate-typed expression), which gets no generated context.
        /// </summary>
        public static string? MethodGroupName(ExpressionSyntax handler) => handler switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => null,
        };

        /// <summary>The single parameter name of a column-selector lambda (<c>a =&gt; ...</c> or <c>(a) =&gt; ...</c>), or null.</summary>
        public static string? SelectorParameter(LambdaExpressionSyntax lambda) => lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
            ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1
                => parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
            _ => null,
        };

        /// <summary>
        /// Sees through leading conversions on a column-selector body: an explicit cast (<c>(object)a.X</c>), an
        /// <c>as</c> (<c>a.X as object</c>), parentheses, and any nesting of them.
        /// </summary>
        public static ExpressionSyntax UnwrapConversions(ExpressionSyntax body)
        {
            while (true)
            {
                switch (body)
                {
                    case CastExpressionSyntax cast:
                        body = cast.Expression;
                        continue;
                    case BinaryExpressionSyntax asExpression when asExpression.IsKind(SyntaxKind.AsExpression):
                        body = asExpression.Left;
                        continue;
                    case ParenthesizedExpressionSyntax parenthesized:
                        body = parenthesized.Expression;
                        continue;
                }

                return body;
            }
        }

        /// <summary>
        /// The message a type-stated trigger (<c>On&lt;TRequest&gt;()</c>, <c>OnAsyncOnly&lt;TRequest&gt;()</c>,
        /// <c>OnMessage&lt;TMessage&gt;()</c>) names, when it is known statically: a <c>[SdkMessage("...")]</c> or
        /// <c>[CustomMessage("...")]</c> on the type, else an SDK request type's name without its <c>Request</c> suffix
        /// (<c>Microsoft.Xrm.Sdk.Messages.CreateRequest</c> is <c>Create</c>). Null for anything else, including a
        /// generated request, which sets its <c>RequestName</c> in a constructor the analyzer does not run.
        /// </summary>
        public static string? TypeStatedMessage(ChainCall call, SemanticModel model, System.Threading.CancellationToken cancellationToken)
        {
            if (call.MethodName is not ("On" or "OnAsyncOnly" or "OnMessage")
                || call.Name is not GenericNameSyntax generic
                || generic.TypeArgumentList.Arguments.Count != 1
                || model.GetTypeInfo(generic.TypeArgumentList.Arguments[0], cancellationToken).Type is not INamedTypeSymbol type)
            {
                return null;
            }

            foreach (AttributeData attribute in type.GetAttributes())
            {
                string? attributeName = attribute.AttributeClass?.ToDisplayString();
                if ((attributeName == "Vela.Messages.SdkMessageAttribute" || attributeName == "Vela.Constants.CustomMessageAttribute")
                    && attribute.ConstructorArguments.Length == 1
                    && attribute.ConstructorArguments[0].Value is string declared)
                {
                    return declared;
                }
            }

            string ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            const string suffix = "Request";
            if ((ns == "Microsoft.Xrm.Sdk.Messages" || ns == "Microsoft.Crm.Sdk.Messages")
                && type.Name.Length > suffix.Length
                && type.Name.EndsWith(suffix, System.StringComparison.Ordinal))
            {
                return type.Name.Substring(0, type.Name.Length - suffix.Length);
            }

            return null;
        }

        /// <summary>
        /// The sdkmessage a type-stated trigger registers on, as the runtime builder computes it: a
        /// <c>[SdkMessage("...")]</c> on the type verbatim, else <see cref="TypeStatedMessage"/> mapped through
        /// <c>SdkMessageNames</c> (<c>SendEmailRequest</c> and <c>SendFaxRequest</c> both register on <c>Send</c>,
        /// <c>SetStateRequest</c> on <c>SetStateDynamicEntity</c>). Null when the message is not known statically.
        /// </summary>
        public static string? TypeStatedSdkMessage(ChainCall call, SemanticModel model, System.Threading.CancellationToken cancellationToken)
        {
            if (TypeStatedMessage(call, model, cancellationToken) is not { } message)
            {
                return null;
            }

            ITypeSymbol type = model.GetTypeInfo(((GenericNameSyntax)call.Name).TypeArgumentList.Arguments[0], cancellationToken).Type!;
            AttributeData? pinned = type.GetAttributes().FirstOrDefault(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Vela.Messages.SdkMessageAttribute"
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string);
            return pinned is not null ? (string)pinned.ConstructorArguments[0].Value! : Vela.Steps.SdkMessageNames.ToSdkMessage(message);
        }

        /// <summary>
        /// The column a selector lambda reads directly on its parameter (<c>a =&gt; a.Name</c>, through conversions),
        /// as the member-access node, or null for anything else (a nested path, a method call, a constant).
        /// </summary>
        public static MemberAccessExpressionSyntax? SelectedMember(LambdaExpressionSyntax lambda)
        {
            string? parameter = SelectorParameter(lambda);
            if (parameter is null || lambda.Body is not ExpressionSyntax body)
            {
                return null;
            }

            if (UnwrapConversions(body) is MemberAccessExpressionSyntax member &&
                UnwrapConversions(member.Expression) is IdentifierNameSyntax receiver &&
                receiver.Identifier.ValueText == parameter)
            {
                return member;
            }

            return null;
        }
    }

    /// <summary>One call on a fluent chain's receiver spine (see <see cref="FluentStep.Spine"/>).</summary>
    internal readonly struct ChainCall
    {
        public ChainCall(InvocationExpressionSyntax invocation, SimpleNameSyntax name)
        {
            Invocation = invocation;
            Name = name;
        }

        /// <summary>The call itself (<c>receiver.Method(args)</c> or the unqualified root <c>Method(args)</c>).</summary>
        public InvocationExpressionSyntax Invocation { get; }

        /// <summary>The method-name node (an identifier, or a generic name such as <c>On&lt;TRequest&gt;</c>).</summary>
        public SimpleNameSyntax Name { get; }

        public string MethodName => Name.Identifier.ValueText;

        public SeparatedSyntaxList<ArgumentSyntax> Arguments => Invocation.ArgumentList.Arguments;
    }
}
