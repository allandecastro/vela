using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vela.Analyzers
{
    /// <summary>
    /// Shared recognition of a Dataverse plugin / Custom API type: one that implements
    /// <c>Microsoft.Xrm.Sdk.IPlugin</c> or derives from a Vela base (<c>Vela.Plugin.PluginBase</c>,
    /// <c>Vela.Plugin.EntityPluginBase&lt;T&gt;</c>, <c>Vela.CustomApi.CustomApiBase</c>). The types are resolved
    /// from the compilation once and compared by symbol, so a user type that merely shares a simple name
    /// (a <c>PluginBase</c> of an unrelated library) is not mistaken for one. Used to scope the plugin-authoring
    /// analyzers (mutable state, exception type, filesystem access) and to read a plugin's entity.
    /// </summary>
    internal sealed class PluginTypes
    {
        public const string PluginBaseMetadataName = "Vela.Plugin.PluginBase";
        public const string EntityPluginBaseMetadataName = "Vela.Plugin.EntityPluginBase`1";
        public const string CustomApiBaseMetadataName = "Vela.CustomApi.CustomApiBase";
        public const string PluginInterfaceMetadataName = "Microsoft.Xrm.Sdk.IPlugin";

        private readonly INamedTypeSymbol? _plugin;
        private readonly INamedTypeSymbol? _pluginBase;
        private readonly INamedTypeSymbol? _entityPluginBase;
        private readonly INamedTypeSymbol? _customApiBase;

        private PluginTypes(Compilation compilation)
        {
            _plugin = compilation.GetTypeByMetadataName(PluginInterfaceMetadataName);
            _pluginBase = compilation.GetTypeByMetadataName(PluginBaseMetadataName);
            _entityPluginBase = compilation.GetTypeByMetadataName(EntityPluginBaseMetadataName);
            _customApiBase = compilation.GetTypeByMetadataName(CustomApiBaseMetadataName);
        }

        /// <summary>
        /// The plugin types of <paramref name="compilation"/>, or null when it references neither the Dataverse SDK
        /// nor Vela (then no type can be a plugin, so the plugin analyzers register nothing).
        /// </summary>
        public static PluginTypes? From(Compilation compilation)
        {
            PluginTypes types = new PluginTypes(compilation);
            return types._plugin is null && types._pluginBase is null && types._entityPluginBase is null && types._customApiBase is null
                ? null
                : types;
        }

        /// <summary><c>Vela.Plugin.PluginBase</c>, when the compilation references Vela.</summary>
        public INamedTypeSymbol? PluginBase => _pluginBase;

        public bool IsPluginType(INamedTypeSymbol? type)
        {
            if (type == null)
            {
                return false;
            }

            if (_plugin is not null)
            {
                foreach (INamedTypeSymbol iface in type.AllInterfaces)
                {
                    if (SymbolEqualityComparer.Default.Equals(iface, _plugin))
                    {
                        return true;
                    }
                }
            }

            for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType)
            {
                INamedTypeSymbol definition = t.OriginalDefinition;
                if (Is(definition, _pluginBase) || Is(definition, _entityPluginBase) || Is(definition, _customApiBase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the innermost type declaration around <paramref name="node"/> is a plugin type. Scopes the
        /// syntax-level plugin analyzers (filesystem access, retrieve-all-columns) to plugin code, so a test or tool
        /// in the same solution is left alone.
        /// </summary>
        public bool Encloses(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            for (SyntaxNode? current = node; current is not null; current = current.Parent)
            {
                if (current is TypeDeclarationSyntax type)
                {
                    return IsPluginType(model.GetDeclaredSymbol(type, cancellationToken) as INamedTypeSymbol);
                }
            }

            return false;
        }

        /// <summary>The <c>TEntity</c> of the <c>EntityPluginBase&lt;TEntity&gt;</c> a plugin derives from, or null.</summary>
        public static INamedTypeSymbol? EntityOf(INamedTypeSymbol? plugin, Compilation compilation)
            => EntityOf(plugin, compilation.GetTypeByMetadataName(EntityPluginBaseMetadataName));

        /// <inheritdoc cref="EntityOf(INamedTypeSymbol?, Compilation)"/>
        public static INamedTypeSymbol? EntityOf(INamedTypeSymbol? plugin, INamedTypeSymbol? entityPluginBase)
        {
            if (entityPluginBase is null)
            {
                return null;
            }

            for (INamedTypeSymbol? t = plugin?.BaseType; t is not null; t = t.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, entityPluginBase))
                {
                    return t.TypeArguments.Length == 1 ? t.TypeArguments[0] as INamedTypeSymbol : null;
                }
            }

            return null;
        }

        /// <summary>True when <paramref name="plugin"/> derives from <c>EntityPluginBase&lt;TEntity&gt;</c>.</summary>
        public static bool IsEntityPlugin(INamedTypeSymbol? plugin, INamedTypeSymbol? entityPluginBase)
        {
            if (entityPluginBase is null)
            {
                return false;
            }

            for (INamedTypeSymbol? t = plugin?.BaseType; t is not null; t = t.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, entityPluginBase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Is(INamedTypeSymbol type, INamedTypeSymbol? target)
            => target is not null && SymbolEqualityComparer.Default.Equals(type, target);
    }
}
