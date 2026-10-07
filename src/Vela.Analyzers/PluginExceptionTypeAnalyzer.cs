using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags throwing an exception other than <c>InvalidPluginExecutionException</c> from a plugin / Custom API
    /// type. Only <c>InvalidPluginExecutionException</c> surfaces its message to the caller (and the UI); any other
    /// exception is reported to the user as a generic, unhelpful error. Rethrows (<c>throw;</c> / <c>throw ex;</c>)
    /// are left alone.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PluginExceptionTypeAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.PluginExceptionType;

        private const string InvalidPluginExecutionExceptionMetadataName = "Microsoft.Xrm.Sdk.InvalidPluginExecutionException";
        private const string PluginBusinessExceptionDisplayName = "Vela.Plugin.PluginBusinessException";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Throw InvalidPluginExecutionException",
            messageFormat: "Throw InvalidPluginExecutionException from a plugin instead of '{0}', so the message reaches the caller",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Only InvalidPluginExecutionException surfaces its message to the caller; any other exception becomes a generic error. Throw InvalidPluginExecutionException for user-facing failures in plugin code.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol? ipee = start.Compilation.GetTypeByMetadataName(InvalidPluginExecutionExceptionMetadataName);
                if (ipee == null)
                {
                    return; // not a Dataverse plugin project
                }

                // Never null here (the SDK's IPlugin sits beside InvalidPluginExecutionException); PluginBase may be
                // null when the project does not reference Vela.Plugins (then no PluginBusinessException exemption).
                PluginTypes? plugins = PluginTypes.From(start.Compilation);
                if (plugins is null)
                {
                    return;
                }

                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeThrow(nodeContext, ipee, plugins), SyntaxKind.ThrowStatement, SyntaxKind.ThrowExpression);
            });
        }

        private static void AnalyzeThrow(SyntaxNodeAnalysisContext context, INamedTypeSymbol ipee, PluginTypes plugins)
        {
            ExpressionSyntax? thrown = context.Node switch
            {
                ThrowStatementSyntax statement => statement.Expression,
                ThrowExpressionSyntax expression => expression.Expression,
                _ => null,
            };

            // Only `throw new SomeException(...)` is a fresh throw; `throw;` and `throw ex;` are rethrows.
            if (thrown is not ObjectCreationExpressionSyntax creation)
            {
                return;
            }

            if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is not INamedTypeSymbol thrownType)
            {
                return;
            }

            if (IsOrDerivesFrom(thrownType, ipee))
            {
                return;
            }

            INamedTypeSymbol? enclosing = EnclosingType(context.Node, context.SemanticModel, context.CancellationToken);
            if (!plugins.IsPluginType(enclosing))
            {
                return;
            }

            // PluginBusinessException is the sanctioned business-error type ONLY inside a PluginBase-derived plugin
            // (or Custom API base): that base's catch turns it into a clean InvalidPluginExecutionException for the
            // caller. A type that implements IPlugin directly has no such frame, so a PluginBusinessException thrown
            // there reaches Dataverse as an ordinary exception - keep flagging it for those. It cannot derive from
            // the sealed SDK exception, so it is recognized by name.
            if (plugins.PluginBase is { } pluginBase
                && thrownType.ToDisplayString() == PluginBusinessExceptionDisplayName
                && IsOrDerivesFrom(enclosing!, pluginBase))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.Type.GetLocation(), thrownType.Name));
        }

        private static bool IsOrDerivesFrom(INamedTypeSymbol type, INamedTypeSymbol target)
        {
            for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(t, target))
                {
                    return true;
                }
            }

            return false;
        }

        private static INamedTypeSymbol? EnclosingType(SyntaxNode node, SemanticModel model, System.Threading.CancellationToken cancellationToken)
        {
            for (SyntaxNode? current = node; current is not null; current = current.Parent)
            {
                if (current is TypeDeclarationSyntax type)
                {
                    return model.GetDeclaredSymbol(type, cancellationToken);
                }
            }

            return null;
        }
    }
}
