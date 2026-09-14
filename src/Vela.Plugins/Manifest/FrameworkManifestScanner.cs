using System;
using System.Collections.Generic;
using System.Reflection;
using Vela.CustomApi;
using Vela.Plugin;
using Vela.StepBuilder;

namespace Vela.Manifest;

/// <summary>
/// Scans an assembly for plugin steps and Custom APIs.
/// </summary>
public static class FrameworkManifestScanner
{
    /// <summary>
    /// Scans the specified assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="options">The scanner options.</param>
    /// <returns>The discovered framework manifest.</returns>
    public static FrameworkManifest Scan(Assembly assembly, FrameworkManifestScanOptions? options = null)
    {
        if (assembly == null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        List<StepDefinition> pluginSteps = new List<StepDefinition>();
        List<CustomApiDefinition> customApis = new List<CustomApiDefinition>();
        List<FrameworkManifestDiagnostic> diagnostics = new List<FrameworkManifestDiagnostic>();

        foreach (Type type in GetLoadableTypes(assembly))
        {
            if (TryGetPluginSteps(type, out StepDefinition[]? steps, diagnostics))
            {
                pluginSteps.AddRange(steps!);
            }

            if (TryGetCustomApi(type, out CustomApiDefinition? customApi, diagnostics))
            {
                customApis.Add(customApi!);
            }
        }

        FrameworkManifestScanOptions effectiveOptions = options ?? new FrameworkManifestScanOptions();
        if (effectiveOptions.Strict && diagnostics.Count > 0)
        {
            throw new InvalidOperationException($"Strict scan failed with {diagnostics.Count} diagnostic(s).");
        }

        return new FrameworkManifest
        {
            SourceAssemblyName = assembly.FullName ?? assembly.GetName().Name ?? string.Empty,
            GeneratedAtUtc = DateTime.UtcNow,
            PluginSteps = pluginSteps.ToArray(),
            CustomApis = customApis.ToArray(),
            Diagnostics = diagnostics.ToArray()
        };
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            List<Type> types = new List<Type>();
            foreach (Type? type in ex.Types)
            {
                if (type != null)
                {
                    types.Add(type);
                }
            }

            return types;
        }
    }

    private static bool TryGetPluginSteps(Type type, out StepDefinition[]? steps, ICollection<FrameworkManifestDiagnostic> diagnostics)
    {
        steps = null;
        if (!PluginTypeDetector.IsEntityPlugin(type))
        {
            return false;
        }

        try
        {
            var instance = PluginTypeDetector.CreateInstance(type);
            MethodInfo? method = instance.GetType().GetMethod("GetStepDefinitions");
            if (method != null && method.Invoke(instance, null) is StepDefinition[] multipleSteps)
            {
                steps = multipleSteps;
                return steps.Length > 0;
            }

            MethodInfo? singleMethod = instance.GetType().GetMethod("GetStepDefinition");
            if (singleMethod != null && singleMethod.Invoke(instance, null) is StepDefinition singleStep)
            {
                steps = [singleStep];
                return true;
            }
        }
        catch (Exception ex)
        {
            // A type that IS a plugin but fails to yield its step definition is excluded from the manifest,
            // which silently drops the step from the plan (and could delete a live registration). Surface it as
            // an Error, not a skippable Warning.
            diagnostics.Add(new FrameworkManifestDiagnostic
            {
                Severity = FrameworkManifestDiagnosticSeverity.Error,
                TypeName = type.FullName ?? type.Name,
                Message = $"Plugin step discovery failed ({ex.GetType().Name}): {ex.Message}"
            });
        }

        return false;
    }

    private static bool TryGetCustomApi(Type type, out CustomApiDefinition? customApi, ICollection<FrameworkManifestDiagnostic> diagnostics)
    {
        customApi = null;
        if (!PluginTypeDetector.IsCustomApi(type))
        {
            return false;
        }

        try
        {
            var instance = PluginTypeDetector.CreateInstance(type);
            if (instance is CustomApiBase customApiBase)
            {
                customApi = customApiBase.GetCustomApiDefinition();
                return customApi != null;
            }
        }
        catch (Exception ex)
        {
            // A type that IS a Custom API but fails to build its definition is excluded from the manifest,
            // which silently drops the whole API from the plan (and could delete a live registration). Surface
            // it as an Error, not a skippable Warning.
            diagnostics.Add(new FrameworkManifestDiagnostic
            {
                Severity = FrameworkManifestDiagnosticSeverity.Error,
                TypeName = type.FullName ?? type.Name,
                Message = $"Custom API discovery failed ({ex.GetType().Name}): {ex.Message}"
            });
        }

        return false;
    }
}
