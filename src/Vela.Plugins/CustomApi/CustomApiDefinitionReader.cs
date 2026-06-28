using System;
using System.Collections.Generic;
using System.Reflection;
using Vela.Manifest;

namespace Vela.CustomApi;

/// <summary>
/// Scans assemblies for Custom API plugin definitions.
/// </summary>
public static class CustomApiDefinitionReader
{
    /// <summary>
    /// Gets all Custom API definitions from an assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The discovered Custom API definitions.</returns>
    public static IReadOnlyList<CustomApiDefinition> GetCustomApiDefinitions(Assembly assembly)
    {
        if (assembly == null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        List<CustomApiDefinition> definitions = new List<CustomApiDefinition>();
        foreach (Type type in assembly.GetTypes())
        {
            if (!PluginTypeDetector.IsCustomApi(type))
            {
                continue;
            }

            var definition = CreateDefinition(type);
            if (definition != null)
            {
                definitions.Add(definition);
            }
        }

        return definitions;
    }

    private static CustomApiDefinition? CreateDefinition(Type pluginType)
    {
        var constructor = PluginTypeDetector.GetStandardConstructor(pluginType);
        if (constructor == null)
        {
            return null;
        }

        object? instance = constructor.Invoke(new object?[] { null, null });
        if (instance is not CustomApiBase customApi)
        {
            return null;
        }

        return customApi.GetCustomApiDefinition();
    }
}
