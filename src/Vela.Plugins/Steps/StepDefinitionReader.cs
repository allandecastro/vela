using System;
using System.Collections.Generic;
using System.Linq;
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
        /// Gets all step definitions from plugins in the specified assembly. A plugin whose step definitions cannot be
        /// read, or a type that fails to load, fails the call with every such error listed; use
        /// <see cref="GetStepDefinitions(Assembly, ICollection{FrameworkManifestDiagnostic})"/> to collect them instead.
        /// </summary>
        /// <param name="assembly">The assembly to scan for plugins.</param>
        /// <returns>Collection of step definitions.</returns>
        /// <exception cref="InvalidOperationException">A plugin or a type in the assembly could not be read.</exception>
        public static IEnumerable<StepDefinition> GetStepDefinitions(Assembly assembly)
        {
            List<FrameworkManifestDiagnostic> diagnostics = new List<FrameworkManifestDiagnostic>();
            IReadOnlyList<StepDefinition> definitions = GetStepDefinitions(assembly, diagnostics);
            if (diagnostics.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Failed to read the step definitions of '{assembly.GetName().Name}':" + Environment.NewLine +
                    string.Join(Environment.NewLine, diagnostics.Select(d => $"- {d.TypeName}: {d.Message}")));
            }

            return definitions;
        }

        /// <summary>
        /// Gets all step definitions from plugins in the specified assembly, best-effort: a plugin whose step
        /// definitions cannot be read (its constructor or step build throws), or a type that fails to load, is
        /// skipped and reported in <paramref name="diagnostics"/> as an error, like <see cref="FrameworkManifestScanner"/>.
        /// </summary>
        /// <param name="assembly">The assembly to scan for plugins.</param>
        /// <param name="diagnostics">Receives one error per plugin or type that could not be read.</param>
        /// <returns>The step definitions of every plugin that could be read.</returns>
        public static IReadOnlyList<StepDefinition> GetStepDefinitions(Assembly assembly, ICollection<FrameworkManifestDiagnostic> diagnostics)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            if (diagnostics == null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }

            var definitions = new List<StepDefinition>();

            foreach (var type in GetLoadableTypes(assembly, diagnostics))
            {
                if (!PluginTypeDetector.IsEntityPlugin(type))
                {
                    continue;
                }

                StepDefinition[]? definition;
                try
                {
                    definition = GetStepDefinitionFromType(type);
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new FrameworkManifestDiagnostic
                    {
                        Severity = FrameworkManifestDiagnosticSeverity.Error,
                        TypeName = type.FullName ?? type.Name,
                        Message = $"Plugin step discovery failed ({(ex.InnerException ?? ex).GetType().Name}): {(ex.InnerException ?? ex).Message}",
                    });
                    continue;
                }

                if (definition != null)
                {
                    definitions.AddRange(definition);
                }
            }

            return definitions;
        }

        // A type that fails to load (a missing dependency) cannot be inspected; the others still are, and the failure
        // is reported instead of escaping as a ReflectionTypeLoadException.
        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly, ICollection<FrameworkManifestDiagnostic> diagnostics)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (string message in ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct())
                {
                    diagnostics.Add(new FrameworkManifestDiagnostic
                    {
                        Severity = FrameworkManifestDiagnosticSeverity.Error,
                        TypeName = assembly.GetName().Name ?? string.Empty,
                        Message = $"A type could not be loaded: {message}",
                    });
                }

                return ex.Types.Where(t => t != null).Select(t => t!).ToArray();
            }
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
