using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags an entity plugin that declares a typed image capture (<c>WithPreImage(...)</c> / <c>WithPostImage(...)</c>
    /// with column selectors) but whose class is not <c>partial</c>. The source generator emits the projection type as
    /// a nested type inside the plugin's own class (so the handler can read it unqualified via
    /// <c>ctx.PostImageAs&lt;OnAccountUpdated_PostImage&gt;()</c>), which needs a second <c>partial</c> declaration of
    /// the class. Without <c>partial</c> the build fails with an obscure CS0260; this reports it up front, and the
    /// code fix adds the modifier.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PartialPluginForTypedImageAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA014";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Plugin with typed images must be partial",
            messageFormat: "Plugin '{0}' captures a typed image, so its class must be declared 'partial' (the generated projection is nested inside it)",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A step that captures WithPreImage/WithPostImage columns gets a compiler-generated projection type nested inside the plugin's own class, so the class needs a matching partial declaration. Declare the plugin class 'partial'.");

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

                start.RegisterSyntaxNodeAction(nodeContext => AnalyzeClass(nodeContext, fluent), SyntaxKind.ClassDeclaration);
            });
        }

        private static void AnalyzeClass(SyntaxNodeAnalysisContext context, INamedTypeSymbol fluent)
        {
            ClassDeclarationSyntax declaration = (ClassDeclarationSyntax)context.Node;

            // Already partial -> the generator's nested part compiles fine, nothing to report.
            if (declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return;
            }

            if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol symbol ||
                !DerivesFromEntityPluginBase(symbol))
            {
                return;
            }

            // Only entity plugins that actually capture a typed image (a WithPreImage/WithPostImage with a column
            // selector lambda) get a generated nested projection, so only those need to be partial.
            if (!DeclaresTypedImageCapture(declaration, context.SemanticModel, fluent, context.CancellationToken))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, declaration.Identifier.GetLocation(), symbol.Name));
        }

        private static bool DerivesFromEntityPluginBase(INamedTypeSymbol symbol)
        {
            for (INamedTypeSymbol? t = symbol.BaseType; t is not null; t = t.BaseType)
            {
                if (t.Name == "EntityPluginBase")
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DeclaresTypedImageCapture(ClassDeclarationSyntax declaration, SemanticModel model, INamedTypeSymbol fluent, System.Threading.CancellationToken cancellationToken)
        {
            foreach (InvocationExpressionSyntax invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member)
                {
                    continue;
                }

                string name = member.Name.Identifier.ValueText;
                if (name != "WithPreImage" && name != "WithPostImage")
                {
                    continue;
                }

                // A column-selector lambda is what triggers a generated projection; a capture with no selectors keeps
                // the full entity and generates nothing (so it does not force partial).
                if (!invocation.ArgumentList.Arguments.Any(a => a.Expression is LambdaExpressionSyntax))
                {
                    continue;
                }

                if (FluentStep.IsFluentMethod(model, invocation, fluent, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
