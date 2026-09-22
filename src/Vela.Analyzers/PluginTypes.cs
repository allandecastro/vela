using Microsoft.CodeAnalysis;

namespace Vela.Analyzers
{
    /// <summary>
    /// Shared recognition of a Dataverse plugin / Custom API type: one that implements
    /// <c>Microsoft.Xrm.Sdk.IPlugin</c> or derives from a Vela base (<c>PluginBase</c>, <c>EntityPluginBase</c>,
    /// <c>CustomApiBase</c>). Used to scope the plugin-authoring analyzers (mutable state, exception type, ...).
    /// </summary>
    internal static class PluginTypes
    {
        public static bool IsPluginType(INamedTypeSymbol? type)
        {
            if (type == null)
            {
                return false;
            }

            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                if (iface.Name == "IPlugin" && iface.ContainingNamespace?.ToDisplayString() == "Microsoft.Xrm.Sdk")
                {
                    return true;
                }
            }

            for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType)
            {
                if (t.Name is "PluginBase" or "EntityPluginBase" or "CustomApiBase")
                {
                    return true;
                }
            }

            return false;
        }
    }
}
