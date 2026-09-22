using System;
using System.Collections.Generic;
using System.Reflection;
using Vela.Manifest;
using Vela.Plugin;

namespace Vela.Steps
{
    /// <summary>
    /// Reads step definitions from plugin assemblies for registration manifest generation.
    /// </summary>
    public static class StepDefinitionReader
    {
        /// <summary>
        /// Gets all step definitions from plugins in the specified assembly.
        /// </summary>
        /// <param name="assembly">The assembly to scan for plugins.</param>
        /// <returns>Collection of step definitions.</returns>
        public static IEnumerable<StepDefinition> GetStepDefinitions(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            var definitions = new List<StepDefinition>();

            foreach (var type in assembly.GetTypes())
            {
                if (!PluginTypeDetector.IsEntityPlugin(type))
                {
                    continue;
                }

                // Assembly-wide scan is best-effort: a single plugin whose step build throws (e.g. one that
                // registers nothing) is skipped rather than failing the whole scan. A direct caller of
                // GetStepDefinitionFromType still gets the exception surfaced.
                StepDefinition[]? definition;
                try
                {
                    definition = GetStepDefinitionFromType(type);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (definition != null)
                {
                    definitions.AddRange(definition);
                }
            }

            return definitions;
        }

        /// <summary>
        /// Gets a step definition from a specific plugin type.
        /// </summary>
        /// <param name="pluginType">The plugin type.</param>
        /// <returns>The step definition, or null if not applicable.</returns>
        public static StepDefinition[]? GetStepDefinitionFromType(Type pluginType)
        {
            if (pluginType == null)
            {
                throw new ArgumentNullException(nameof(pluginType));
            }

            if (!PluginTypeDetector.IsEntityPlugin(pluginType))
            {
                return null;
            }

            try
            {
                // Try to create instance with standard (unsecure, secure) constructor
                var constructor = PluginTypeDetector.GetStandardConstructor(pluginType);
                if (constructor == null)
                {
                    return null;
                }

                var instance = constructor.Invoke(new object?[] { null, null });

                var getStepDefinitionsMethod = pluginType.GetMethod("GetStepDefinitions", BindingFlags.Public | BindingFlags.Instance);
                if (getStepDefinitionsMethod != null &&
                    getStepDefinitionsMethod.Invoke(instance, null) is StepDefinition[] definitions &&
                    definitions.Length > 0)
                {
                    return definitions;
                }

                var getStepDefinitionMethod = pluginType.GetMethod("GetStepDefinition", BindingFlags.Public | BindingFlags.Instance);
                if (getStepDefinitionMethod == null)
                {
                    return null;
                }

                if (getStepDefinitionMethod.Invoke(instance, null) is StepDefinition definition)
                {
                    return [definition];
                }

                return null;
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                // The plugin's step build (ctor / GetStepDefinitions) threw - surface it instead of silently
                // dropping the plugin, so a real configuration error (missing FilterOn, duplicate trigger, no
                // handler) is not swallowed and reported as "no steps".
                throw new System.InvalidOperationException(
                    $"Failed to read step definitions from plugin '{pluginType.FullName}': {ex.InnerException?.Message ?? ex.Message}",
                    ex.InnerException ?? ex);
            }
        }
    }
}
