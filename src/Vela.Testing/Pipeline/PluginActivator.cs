using System;
using System.Reflection;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing
{
    // Creates plugin instances the way the platform does: through the (string unsecureConfig, string secureConfig)
    // constructor every Vela plugin and Custom API exposes. The one place the harness resolves that constructor.
    internal static class PluginActivator
    {
        private static readonly Type[] ConfigurationSignature = { typeof(string), typeof(string) };

        // The (string, string) constructor, or null when the type has none.
        public static ConstructorInfo? FindConfigurationConstructor(Type pluginType)
        {
            return pluginType.GetConstructor(ConfigurationSignature);
        }

        // The (string, string) constructor, or an InvalidOperationException naming the type and the reason.
        public static ConstructorInfo RequireConfigurationConstructor(Type pluginType, string purpose)
        {
            return FindConfigurationConstructor(pluginType)
                ?? throw new InvalidOperationException(
                    $"Plugin '{pluginType.FullName}' must have a (string unsecureConfig, string secureConfig) constructor {purpose}.");
        }

        public static IPlugin Create(ConstructorInfo constructor, string? unsecureConfig, string? secureConfig)
        {
            return (IPlugin)constructor.Invoke(new object?[] { unsecureConfig, secureConfig });
        }
    }
}
