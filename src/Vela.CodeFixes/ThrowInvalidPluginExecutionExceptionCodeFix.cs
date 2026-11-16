using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vela.CodeFixes
{
    /// <summary>Changes the thrown exception to <c>InvalidPluginExecutionException</c> (VELA012).</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ThrowInvalidPluginExecutionExceptionCodeFix)), Shared]
    public sealed class ThrowInvalidPluginExecutionExceptionCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.PluginExceptionType);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

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
                ObjectCreationExpressionSyntax? creation = root.FindNode(diagnostic.Location.SourceSpan)
                    .FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
                if (creation == null || Arguments(creation, model, context.CancellationToken) is not { } arguments)
                {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        "Throw InvalidPluginExecutionException",
                        ct => ReplaceAsync(context.Document, creation, arguments, ct),
                        equivalenceKey: "ThrowInvalidPluginExecutionException"),
                    diagnostic);
            }
        }

        // The arguments of the InvalidPluginExecutionException that replaces the creation, for its (), (string message)
        // or (string message, Exception inner) constructor, positional. Each original argument is bound to its
        // constructor parameter (by name for a named argument), so only the real message is kept: a paramName
        // (new ArgumentNullException(nameof(x))) is not a message. An inner exception alone becomes
        // (inner.Message, inner). Null when no fix is offered: an inner exception that is not a plain variable or member,
        // which (inner.Message, inner) would evaluate twice.
        private static IReadOnlyList<ExpressionSyntax>? Arguments(ObjectCreationExpressionSyntax creation, SemanticModel model, CancellationToken cancellationToken)
        {
            ExpressionSyntax? message = null;
            ExpressionSyntax? inner = null;
            SymbolInfo info = model.GetSymbolInfo(creation, cancellationToken);
            if ((info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) is IMethodSymbol constructor)
            {
                SeparatedSyntaxList<ArgumentSyntax> arguments = creation.ArgumentList?.Arguments ?? default;
                for (int i = 0; i < arguments.Count; i++)
                {
                    IParameterSymbol? parameter = ParameterOf(constructor, arguments[i], i);
                    if (parameter == null)
                    {
                        continue;
                    }

                    if (message == null && IsMessage(parameter))
                    {
                        message = arguments[i].Expression;
                    }
                    else if (inner == null && IsException(parameter.Type))
                    {
                        inner = arguments[i].Expression;
                    }
                }
            }

            if (message != null)
            {
                return inner != null ? new[] { message, inner } : new[] { message };
            }

            if (inner == null)
            {
                return new ExpressionSyntax[0];
            }

            if (!IsPlainReference(inner))
            {
                return null;
            }

            ExpressionSyntax innerMessage = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression, inner.WithoutTrivia(), SyntaxFactory.IdentifierName("Message"));
            return new[] { innerMessage, inner };
        }

        private static IParameterSymbol? ParameterOf(IMethodSymbol constructor, ArgumentSyntax argument, int position)
        {
            if (argument.NameColon is { } nameColon)
            {
                string name = nameColon.Name.Identifier.ValueText;
                return constructor.Parameters.FirstOrDefault(p => p.Name == name);
            }

            if (position < constructor.Parameters.Length)
            {
                return constructor.Parameters[position];
            }

            return constructor.Parameters.Length > 0 && constructor.Parameters[constructor.Parameters.Length - 1].IsParams
                ? constructor.Parameters[constructor.Parameters.Length - 1]
                : null;
        }

        // The message is the string parameter named for it (message, errorMessage, ...); a paramName, a fileName or any
        // other string is not.
        private static bool IsMessage(IParameterSymbol parameter)
            => parameter.Type.SpecialType == SpecialType.System_String
                && parameter.Name.IndexOf("message", System.StringComparison.OrdinalIgnoreCase) >= 0;

        // A variable, parameter, field or property read (ex, this.inner, state.Error): safe to read twice.
        private static bool IsPlainReference(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax => true,
            ThisExpressionSyntax => true,
            MemberAccessExpressionSyntax member when member.IsKind(SyntaxKind.SimpleMemberAccessExpression) => IsPlainReference(member.Expression),
            ParenthesizedExpressionSyntax parenthesized => IsPlainReference(parenthesized.Expression),
            _ => false,
        };

        private static async Task<Document> ReplaceAsync(Document document, ObjectCreationExpressionSyntax creation, IReadOnlyList<ExpressionSyntax> arguments, CancellationToken cancellationToken)
        {
            SyntaxNode root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;

            // Positional arguments only: the original names (paramName:, innerException:) need not exist on the new type.
            ArgumentListSyntax argumentList = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                arguments.Select(a => SyntaxFactory.Argument(a.WithoutTrivia()))));

            ObjectCreationExpressionSyntax replacement = creation
                .WithType(SyntaxFactory.ParseTypeName("global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException").WithTriviaFrom(creation.Type))
                .WithArgumentList(argumentList.WithTriviaFrom(creation.ArgumentList ?? argumentList))
                .WithInitializer(null);

            return document.WithSyntaxRoot(root.ReplaceNode(creation, replacement));
        }

        private static bool IsException(ITypeSymbol? type)
        {
            for (ITypeSymbol? t = type; t != null; t = t.BaseType)
            {
                if (t.Name == "Exception" && t.ContainingNamespace?.ToDisplayString() == "System")
                {
                    return true;
                }
            }

            return false;
        }
    }
}
