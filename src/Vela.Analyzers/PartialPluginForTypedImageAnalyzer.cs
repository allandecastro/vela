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

            // Already partial -> the generator's nested part compiles fine against this class, nothing to report here.
            if (declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return;
            }

            // The generated projection nests inside the plugin class AND every enclosing class, so each of them must
            // be 'partial'. Report on THIS class when it is the plugin that captures a typed image, or when it
            // (lexically) encloses one - so every class in the chain is flagged, each on its own node (a local
            // diagnostic, which the fix can act on). Reporting only the plugin class would leave a non-partial outer
            // class failing the build with an obscure CS0260.
            string? plugin = FindTypedImagePlugin(declaration, context.SemanticModel, fluent, context.CancellationToken);
            if (plugin is null)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, declaration.Identifier.GetLocation(), declaration.Identifier.ValueText, plugin));
        }

        // The name of the plugin whose generated projection nests inside this class: the class itself when it is a
        // typed-image plugin, otherwise the first nested (any depth) typed-image plugin it encloses. null when none,
        // so this class does not need to be partial.
        private static string? FindTypedImagePlugin(ClassDeclarationSyntax declaration, SemanticModel model, INamedTypeSymbol fluent, System.Threading.CancellationToken cancellationToken)
        {
            if (model.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol self &&
                DerivesFromEntityPluginBase(self) &&
                GeneratesNestedType(declaration, model, fluent, cancellationToken))
            {
                return self.Name;
            }

            foreach (ClassDeclarationSyntax nested in declaration.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(nested, cancellationToken) is INamedTypeSymbol nestedSymbol &&
                    DerivesFromEntityPluginBase(nestedSymbol) &&
                    GeneratesNestedType(nested, model, fluent, cancellationToken))
                {
                    return nestedSymbol.Name;
                }
            }

            return null;
        }

        // The generator nests a type inside the plugin class - a {Handler}Context for any named handler on a modelled
        // trigger, and a projection for any typed image capture - so either forces the plugin to be partial.
        private static bool GeneratesNestedType(ClassDeclarationSyntax declaration, SemanticModel model, INamedTypeSymbol fluent, System.Threading.CancellationToken cancellationToken)
            => DeclaresTypedImageCapture(declaration, model, fluent, cancellationToken)
                || DeclaresNamedHandlerContext(declaration, model, fluent, cancellationToken);

        // A named .Handle(OnX) on a step whose trigger the generator maps to a modelled message (On{Msg}() or
        // On<TRequest>(), but not the permissive OnMessage(...)) produces a nested {Handler}Context. Mirrors the
        // generator's ExtractHandler conditions.
        private static bool DeclaresNamedHandlerContext(ClassDeclarationSyntax declaration, SemanticModel model, INamedTypeSymbol fluent, System.Threading.CancellationToken cancellationToken)
        {
            foreach (InvocationExpressionSyntax invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                    member.Name.Identifier.ValueText != "Handle" ||
                    invocation.ArgumentList.Arguments.Count != 1)
                {
                    continue;
                }

                // Method group only (a lambda has no name, so no context is generated).
                if (invocation.ArgumentList.Arguments[0].Expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
                {
                    continue;
                }

                if (FluentStep.IsFluentMethod(model, invocation, fluent, cancellationToken) &&
                    ChainGeneratesContext(member.Expression))
                {
                    return true;
                }
            }

            return false;
        }

        // Mirrors the generator's exact emit condition, so VELA014 demands 'partial' only when a context is actually
        // generated (the stages whose config exposes Handle(Delegate)):
        //   OnCreate -> PostOperation only;  OnUpdate / OnDelete -> any stage;  On&lt;TRequest&gt;() -> PostOperation only.
        // The permissive OnMessage(...) and generated On{X} Custom API extensions never generate a context.
        private static bool ChainGeneratesContext(ExpressionSyntax? receiver)
        {
            bool onCreate = false, onUpdateOrDelete = false, typed = false, postOperation = false;
            for (ExpressionSyntax? current = receiver; current is not null;)
            {
                if (current is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
                {
                    string name = ma.Name.Identifier.ValueText;
                    if (name == "On" && ma.Name is GenericNameSyntax)
                    {
                        typed = true;
                    }
                    else if (name == "OnCreate")
                    {
                        onCreate = true;
                    }
                    else if (name is "OnUpdate" or "OnDelete")
                    {
                        onUpdateOrDelete = true;
                    }
                    else if (name == "PostOperation")
                    {
                        postOperation = true;
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

            return onUpdateOrDelete || ((onCreate || typed) && postOperation);
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
