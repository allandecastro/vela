using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags mutable state on a plugin / Custom API type. The Dataverse sandbox caches and reuses plugin instances
    /// across executions and calls them concurrently, so mutable state leaks between unrelated operations and races.
    /// Four shapes are flagged: a writable field, a writable auto-property (its backing field is a writable field),
    /// a <c>readonly</c> field that holds a mutable collection (<c>readonly</c> freezes the reference, not the
    /// contents), and a primary-constructor parameter captured by a member (the compiler stores it in a writable
    /// field). Keep per-execution state in locals or the context; use <c>const</c> / <c>readonly</c> and read-only
    /// collections for immutable configuration.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MutablePluginStateAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.MutablePluginState;

        internal const string WritableField = "a writable field";
        internal const string WritableAutoProperty = "a writable auto-property";
        internal const string ReadOnlyMutableCollection = "a readonly field holding a mutable collection";
        internal const string CapturedParameter = "a primary-constructor parameter captured by a member";

        // Mutable collection types, by metadata name. A readonly field of one of these still shares its contents
        // across executions. Arrays (fixed size, the usual column-name lists), the concurrent collections (thread-safe
        // by design, the deliberate cache) and the immutable / read-only collections are left alone.
        private static readonly ImmutableHashSet<string> MutableCollections = ImmutableHashSet.Create(
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.Dictionary`2",
            "System.Collections.Generic.HashSet`1",
            "System.Collections.Generic.SortedDictionary`2",
            "System.Collections.Generic.SortedList`2",
            "System.Collections.Generic.SortedSet`1",
            "System.Collections.Generic.LinkedList`1",
            "System.Collections.Generic.Queue`1",
            "System.Collections.Generic.Stack`1",
            "System.Collections.Generic.ICollection`1",
            "System.Collections.Generic.IList`1",
            "System.Collections.Generic.IDictionary`2",
            "System.Collections.Generic.ISet`1",
            "System.Collections.ArrayList",
            "System.Collections.Hashtable",
            "System.Collections.Queue",
            "System.Collections.Stack",
            "System.Collections.SortedList");

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Mutable plugin state",
            messageFormat: "'{0}' is {1}: mutable state on a plugin type, which the sandbox reuses across executions, so it leaks state and races",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Plugin instances are reused and called concurrently by the Dataverse sandbox. A writable field, a writable auto-property, a readonly field holding a mutable collection or a captured primary-constructor parameter is shared across unrelated executions. Use locals or the execution context for per-call state, and const/readonly fields with immutable values for configuration.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                // Compilation guard: a project that references neither the Dataverse SDK nor Vela has no plugin
                // type, so no member is inspected at all.
                PluginTypes? plugins = PluginTypes.From(start.Compilation);
                if (plugins is null)
                {
                    return;
                }

                start.RegisterSymbolAction(c => AnalyzeField(c, plugins), SymbolKind.Field);
                start.RegisterSymbolAction(c => AnalyzeProperty(c, plugins), SymbolKind.Property);
                start.RegisterSymbolStartAction(c => AnalyzePrimaryConstructor(c, plugins), SymbolKind.NamedType);
            });
        }

        private static void AnalyzeField(SymbolAnalysisContext context, PluginTypes plugins)
        {
            IFieldSymbol field = (IFieldSymbol)context.Symbol;

            // const is immutable; auto-property backing fields and other compiler-generated fields are reported
            // through their property (or not at all).
            if (field.IsConst || field.IsImplicitlyDeclared || field.AssociatedSymbol != null || field.Locations.Length == 0)
            {
                return;
            }

            string? shape = !field.IsReadOnly ? WritableField
                : IsMutableCollection(field.Type) ? ReadOnlyMutableCollection
                : null;
            if (shape is null || !plugins.IsPluginType(field.ContainingType))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0], field.Name, shape));
        }

        private static void AnalyzeProperty(SymbolAnalysisContext context, PluginTypes plugins)
        {
            IPropertySymbol property = (IPropertySymbol)context.Symbol;

            // Only an auto-property with a set accessor stores state: init-only is set once at construction, a
            // get-only auto-property is backed by a readonly field, and a property with accessor bodies stores
            // nothing itself (the field it uses is analyzed on its own).
            if (property.IsIndexer || property.IsAbstract || property.SetMethod is null || property.SetMethod.IsInitOnly ||
                property.Locations.Length == 0 || !IsAutoProperty(property, context))
            {
                return;
            }

            if (!plugins.IsPluginType(property.ContainingType))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, property.Locations[0], property.Name, WritableAutoProperty));
        }

        private static void AnalyzePrimaryConstructor(SymbolStartAnalysisContext context, PluginTypes plugins)
        {
            INamedTypeSymbol type = (INamedTypeSymbol)context.Symbol;
            IMethodSymbol? primary = PrimaryConstructorOf(type, context);
            if (primary is null || primary.Parameters.Length == 0 || !plugins.IsPluginType(type))
            {
                return;
            }

            // A parameter is captured when a member body reads or writes it. A use in the primary constructor
            // itself (base-constructor arguments) or in a field / property initializer runs once at construction
            // and stores nothing.
            ConcurrentDictionary<IParameterSymbol, bool> captured = new ConcurrentDictionary<IParameterSymbol, bool>(SymbolEqualityComparer.Default);
            context.RegisterOperationAction(
                c =>
                {
                    IParameterSymbol parameter = ((IParameterReferenceOperation)c.Operation).Parameter;
                    if (SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, primary) &&
                        !SymbolEqualityComparer.Default.Equals(c.ContainingSymbol, primary) &&
                        c.ContainingSymbol is not IFieldSymbol &&
                        c.ContainingSymbol is not IPropertySymbol)
                    {
                        captured.TryAdd(parameter, true);
                    }
                },
                OperationKind.ParameterReference);

            context.RegisterSymbolEndAction(c =>
            {
                foreach (IParameterSymbol parameter in primary.Parameters)
                {
                    if (captured.ContainsKey(parameter) && parameter.Locations.Length > 0)
                    {
                        c.ReportDiagnostic(Diagnostic.Create(Rule, parameter.Locations[0], parameter.Name, CapturedParameter));
                    }
                }
            });
        }

        private static IMethodSymbol? PrimaryConstructorOf(INamedTypeSymbol type, SymbolStartAnalysisContext context)
        {
            foreach (IMethodSymbol constructor in type.InstanceConstructors)
            {
                foreach (SyntaxReference reference in constructor.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax(context.CancellationToken) is TypeDeclarationSyntax)
                    {
                        return constructor;
                    }
                }
            }

            return null;
        }

        private static bool IsAutoProperty(IPropertySymbol property, SymbolAnalysisContext context)
        {
            foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(context.CancellationToken) is not PropertyDeclarationSyntax { AccessorList: { } accessors, ExpressionBody: null })
                {
                    return false;
                }

                foreach (AccessorDeclarationSyntax accessor in accessors.Accessors)
                {
                    if (accessor.Body is not null || accessor.ExpressionBody is not null)
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        private static bool IsMutableCollection(ITypeSymbol type)
            => type is INamedTypeSymbol { ContainingNamespace: { } ns } named &&
               MutableCollections.Contains(ns.ToDisplayString() + "." + named.OriginalDefinition.MetadataName);
    }
}
