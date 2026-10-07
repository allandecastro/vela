using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a writable field on a plugin / Custom API type. The Dataverse sandbox caches and reuses plugin
    /// instances across executions and calls them concurrently, so a mutable field leaks state between unrelated
    /// operations and races. Keep per-execution state in locals or the context; use <c>const</c> / <c>readonly</c>
    /// for immutable configuration.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MutablePluginStateAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.MutablePluginState;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Mutable plugin state",
            messageFormat: "Field '{0}' is mutable state on a plugin type; the sandbox reuses instances across executions, so this leaks state and races",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Plugin instances are reused and called concurrently by the Dataverse sandbox. A writable field is shared across unrelated executions. Use locals or the execution context for per-call state, and const/readonly for immutable configuration.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                // Compilation guard: a project that references neither the Dataverse SDK nor Vela has no plugin
                // type, so no field is inspected at all.
                PluginTypes? plugins = PluginTypes.From(start.Compilation);
                if (plugins is null)
                {
                    return;
                }

                start.RegisterSymbolAction(c => AnalyzeField(c, plugins), SymbolKind.Field);
            });
        }

        private static void AnalyzeField(SymbolAnalysisContext context, PluginTypes plugins)
        {
            IFieldSymbol field = (IFieldSymbol)context.Symbol;

            // const / readonly are immutable; auto-property backing fields and other compiler-generated fields are
            // not user state. Only a plain writable field is flagged.
            if (field.IsConst || field.IsReadOnly || field.IsImplicitlyDeclared || field.AssociatedSymbol != null)
            {
                return;
            }

            if (!plugins.IsPluginType(field.ContainingType) || field.Locations.Length == 0)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0], field.Name));
        }
    }
}
