using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags an entity plugin into which the source generator nests a type (a <c>{Handler}Context</c> for a named
    /// handler, or a typed image projection for a <c>WithPreImage(...)</c> / <c>WithPostImage(...)</c> capture) but
    /// whose class - or a type enclosing it - is not <c>partial</c>. The generated part reopens each of those types,
    /// so without <c>partial</c> the build fails with an obscure CS0260; this reports it up front, and the code fix
    /// adds the modifier. The decision is the generator's own (<see cref="ImageProjectionGenerator.EmitsNestedType"/>),
    /// so <c>partial</c> is asked for exactly when something is generated.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PartialPluginForTypedImageAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.PartialPluginForTypedImage;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Plugin must be partial",
            messageFormat: "'{0}' must be declared 'partial' so the generated step context (and any typed image projection) for plugin '{1}' can nest inside it",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A named handler gets a compiler-generated {Handler}Context nested inside the plugin's own class (and a step that captures WithPreImage/WithPostImage columns gets a nested projection too), so the plugin class - and every type that encloses it - needs a matching partial declaration. Declare them 'partial'.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                if (start.Compilation.GetTypeByMetadataName(FluentStep.FluentStepMetadataName) is null)
                {
                    return;
                }

                // A plugin is a class, but the types enclosing it can be any kind the generator reopens.
                start.RegisterSyntaxNodeAction(
                    AnalyzeType,
                    SyntaxKind.ClassDeclaration,
                    SyntaxKind.StructDeclaration,
                    SyntaxKind.RecordDeclaration,
                    SyntaxKind.RecordStructDeclaration,
                    SyntaxKind.InterfaceDeclaration);
            });
        }

        private static void AnalyzeType(SyntaxNodeAnalysisContext context)
        {
            TypeDeclarationSyntax declaration = (TypeDeclarationSyntax)context.Node;

            // Already partial -> the generator's nested part compiles fine against this type, nothing to report here.
            if (declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return;
            }

            // The generated part nests inside the plugin class AND every enclosing type, so each of them must be
            // 'partial'. Report on THIS type when it is the plugin the generator emits into, or when it (lexically)
            // encloses one - so every type in the chain is flagged, each on its own node (a local diagnostic, which
            // the fix can act on).
            string? plugin = FindGeneratedPlugin(declaration, context.SemanticModel, context.CancellationToken);
            if (plugin is null)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, declaration.Identifier.GetLocation(), declaration.Identifier.ValueText, plugin));
        }

        // The name of the plugin class (this type or one it encloses, at any depth) that the generator emits a nested
        // type into, or null when it emits none, so this type does not need to be partial.
        private static string? FindGeneratedPlugin(TypeDeclarationSyntax declaration, SemanticModel model, System.Threading.CancellationToken cancellationToken)
        {
            foreach (InvocationExpressionSyntax invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (ImageProjectionGenerator.EmitsNestedType(invocation, model, cancellationToken) &&
                    invocation.FirstAncestorOrSelf<ClassDeclarationSyntax>() is { } plugin)
                {
                    return plugin.Identifier.ValueText;
                }
            }

            return null;
        }
    }
}
