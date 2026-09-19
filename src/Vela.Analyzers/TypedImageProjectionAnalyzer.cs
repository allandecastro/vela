using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a typed-image handler (<c>Handle&lt;TImg&gt;</c> / <c>Handle&lt;TPre, TPost&gt;</c> /
    /// <c>HandleWithPreImage</c> / <c>HandleWithPostImage</c>) whose projection type reads a column the step does
    /// not capture in <c>WithPreImage(...)</c> / <c>WithPostImage(...)</c>. Such a column is never populated, so it
    /// would silently be <c>null</c> at runtime - which is exactly what the typed-image feature exists to prevent.
    /// The usual cause is picking the wrong generated projection (e.g. copied from another step) or a hand-written
    /// one; the fix is to use the projection generated for this step, or to capture the missing column.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class TypedImageProjectionAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA004";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Typed image projection reads an uncaptured column",
            messageFormat: "Image projection '{0}' reads column '{1}', which this step does not capture; it would be null at runtime",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A typed-image handler's projection must only expose columns the step captured in WithPreImage/WithPostImage. Use the projection generated for this step, or capture the missing column.");

        private static readonly ImmutableHashSet<string> TerminalNames = ImmutableHashSet.Create(
            "Handle", "HandleWithPreImage", "HandleWithPostImage");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                !TerminalNames.Contains(member.Name.Identifier.ValueText))
            {
                return;
            }

            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
                method.ContainingType?.Name != "TypedImageHandlers")
            {
                return;
            }

            // The handler delegate parameter (Action<ctx, TImg> / Action<ctx, TPre, TPost>).
            INamedTypeSymbol? action = method.Parameters
                .Select(p => p.Type as INamedTypeSymbol)
                .FirstOrDefault(t => t is not null && t.Name == "Action" && t.TypeArguments.Length >= 2);
            if (action is null)
            {
                return;
            }

            ImmutableArray<ITypeSymbol> typeArguments = action.TypeArguments;
            (ITypeSymbol? preType, ITypeSymbol? postType) = MapImageTypes(member, method.Name, typeArguments, context.SemanticModel, context.CancellationToken);

            // captured == null means "capture-all" (WithPreImage()/WithPostImage() with no columns): every column
            // is present, so nothing to flag for that image.
            HashSet<string>? capturedPre = ExtractCaptured(member.Expression, "WithPreImage", context.SemanticModel, context.CancellationToken);
            HashSet<string>? capturedPost = ExtractCaptured(member.Expression, "WithPostImage", context.SemanticModel, context.CancellationToken);

            Report(context, member, preType, capturedPre);
            Report(context, member, postType, capturedPost);
        }

        private static (ITypeSymbol? Pre, ITypeSymbol? Post) MapImageTypes(
            MemberAccessExpressionSyntax member, string methodName, ImmutableArray<ITypeSymbol> actionArgs, SemanticModel model, System.Threading.CancellationToken ct)
        {
            // actionArgs[0] is the context; the remaining are the projection type(s).
            if (methodName == "HandleWithPreImage")
            {
                return (actionArgs[1], null);
            }

            if (methodName == "HandleWithPostImage")
            {
                return (null, actionArgs[1]);
            }

            if (actionArgs.Length == 3)
            {
                return (actionArgs[1], actionArgs[2]);
            }

            // Single-projection Handle: the image kind is fixed by the config the chain produced.
            // Only Create PostOperation exposes a post-image single Handle; every other single case is a pre-image.
            ITypeSymbol? configType = model.GetTypeInfo(member.Expression, ct).Type;
            bool isPost = configType?.Name.StartsWith("ICreatePost", System.StringComparison.Ordinal) == true;
            return isPost ? (null, actionArgs[1]) : (actionArgs[1], null);
        }

        private static void Report(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax member, ITypeSymbol? projection, HashSet<string>? captured)
        {
            if (projection is null || captured is null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> column in GetProjectionColumns(projection))
            {
                if (!captured.Contains(column.Value))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), projection.Name, column.Value));
                }
            }
        }

        // Property name -> attribute logical name for every [AttributeLogicalName] property on the projection type.
        private static IEnumerable<KeyValuePair<string, string>> GetProjectionColumns(ITypeSymbol projection)
        {
            foreach (ISymbol member in projection.GetMembers())
            {
                if (member is IPropertySymbol property)
                {
                    string? logicalName = GetAttributeLogicalName(property);
                    if (logicalName is not null)
                    {
                        yield return new KeyValuePair<string, string>(property.Name, logicalName);
                    }
                }
            }
        }

        // Union of the logical names captured by every WithPreImage/WithPostImage on the chain spine. Returns null
        // if a capture-all (no-column) call is present, meaning the image carries every column.
        private static HashSet<string>? ExtractCaptured(ExpressionSyntax? receiver, string methodName, SemanticModel model, System.Threading.CancellationToken ct)
        {
            HashSet<string> captured = new HashSet<string>();
            for (ExpressionSyntax? current = receiver; current is not null;)
            {
                if (current is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
                {
                    if (ma.Name.Identifier.ValueText == methodName)
                    {
                        List<LambdaExpressionSyntax> selectors = inv.ArgumentList.Arguments
                            .Select(a => a.Expression)
                            .OfType<LambdaExpressionSyntax>()
                            .ToList();
                        if (selectors.Count == 0)
                        {
                            return null; // capture-all
                        }

                        foreach (LambdaExpressionSyntax selector in selectors)
                        {
                            if (selector.Body is MemberAccessExpressionSyntax access &&
                                model.GetSymbolInfo(access, ct).Symbol is IPropertySymbol property &&
                                GetAttributeLogicalName(property) is string logicalName)
                            {
                                captured.Add(logicalName);
                            }
                        }
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

            return captured;
        }

        private static string? GetAttributeLogicalName(IPropertySymbol property)
        {
            foreach (AttributeData attribute in property.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == "AttributeLogicalNameAttribute" &&
                    attribute.ConstructorArguments.Length == 1 &&
                    attribute.ConstructorArguments[0].Value is string name &&
                    !string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return null;
        }
    }
}
