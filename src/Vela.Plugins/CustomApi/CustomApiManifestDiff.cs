using System;
using System.Collections.Generic;
using System.Linq;

namespace Vela.CustomApi;

/// <summary>
/// Represents the difference between two Custom API manifests.
/// </summary>
public sealed class CustomApiManifestDiff
{
    /// <summary>
    /// Gets or sets the added Custom APIs.
    /// </summary>
    public CustomApiDefinition[] Added { get; set; } = [];

    /// <summary>
    /// Gets or sets the removed Custom APIs.
    /// </summary>
    public CustomApiDefinition[] Removed { get; set; } = [];

    /// <summary>
    /// Gets or sets the changed Custom APIs.
    /// </summary>
    public CustomApiDefinitionChange[] Changed { get; set; } = [];
}

/// <summary>
/// Represents a changed Custom API definition.
/// </summary>
public sealed class CustomApiDefinitionChange
{
    /// <summary>
    /// Gets or sets the Custom API unique name.
    /// </summary>
    public string UniqueName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the before definition.
    /// </summary>
    public CustomApiDefinition? Before { get; set; }

    /// <summary>
    /// Gets or sets the after definition.
    /// </summary>
    public CustomApiDefinition? After { get; set; }

    /// <summary>
    /// Gets or sets the property changes.
    /// </summary>
    public CustomApiPropertyChange[] PropertyChanges { get; set; } = [];
}

/// <summary>
/// Represents a single property change.
/// </summary>
public sealed class CustomApiPropertyChange
{
    /// <summary>
    /// Gets or sets the property name.
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the previous value.
    /// </summary>
    public string? Before { get; set; }

    /// <summary>
    /// Gets or sets the updated value.
    /// </summary>
    public string? After { get; set; }
}

/// <summary>
/// Compares Custom API manifests.
/// </summary>
public static class CustomApiManifestComparer
{
    /// <summary>
    /// Compares desired and current Custom API definitions.
    /// </summary>
    /// <param name="desired">The desired manifest.</param>
    /// <param name="current">The current manifest.</param>
    /// <returns>A diff result.</returns>
    public static CustomApiManifestDiff Compare(IEnumerable<CustomApiDefinition> desired, IEnumerable<CustomApiDefinition> current)
    {
        if (desired == null)
        {
            throw new ArgumentNullException(nameof(desired));
        }

        if (current == null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        Dictionary<string, CustomApiDefinition> desiredMap = desired.ToDictionary(x => x.UniqueName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CustomApiDefinition> currentMap = current.ToDictionary(x => x.UniqueName, StringComparer.OrdinalIgnoreCase);

        List<CustomApiDefinition> added = new List<CustomApiDefinition>();
        List<CustomApiDefinition> removed = new List<CustomApiDefinition>();
        List<CustomApiDefinitionChange> changed = new List<CustomApiDefinitionChange>();

        foreach (KeyValuePair<string, CustomApiDefinition> pair in desiredMap)
        {
            if (!currentMap.TryGetValue(pair.Key, out CustomApiDefinition? currentDefinition))
            {
                added.Add(pair.Value);
                continue;
            }

            var changes = CompareDefinition(pair.Value, currentDefinition);
            if (changes.Length > 0)
            {
                changed.Add(new CustomApiDefinitionChange
                {
                    UniqueName = pair.Key,
                    Before = currentDefinition,
                    After = pair.Value,
                    PropertyChanges = changes
                });
            }
        }

        foreach (KeyValuePair<string, CustomApiDefinition> pair in currentMap)
        {
            if (!desiredMap.ContainsKey(pair.Key))
            {
                removed.Add(pair.Value);
            }
        }

        return new CustomApiManifestDiff
        {
            Added = added.ToArray(),
            Removed = removed.ToArray(),
            Changed = changed.ToArray()
        };
    }

    private static CustomApiPropertyChange[] CompareDefinition(CustomApiDefinition desired, CustomApiDefinition current)
    {
        List<CustomApiPropertyChange> changes = new List<CustomApiPropertyChange>();
        CompareValue(changes, nameof(CustomApiDefinition.DisplayName), desired.DisplayName, current.DisplayName);
        CompareValue(changes, nameof(CustomApiDefinition.Description), desired.Description, current.Description);
        CompareValue(changes, nameof(CustomApiDefinition.IsFunction), desired.IsFunction, current.IsFunction);
        CompareValue(changes, nameof(CustomApiDefinition.IsBound), desired.IsBound, current.IsBound);
        CompareValue(changes, nameof(CustomApiDefinition.BoundEntityLogicalName), desired.BoundEntityLogicalName, current.BoundEntityLogicalName);
        CompareValue(changes, nameof(CustomApiDefinition.ExecutePrivilegeName), desired.ExecutePrivilegeName, current.ExecutePrivilegeName);
        CompareValue(changes, nameof(CustomApiDefinition.IsPrivate), desired.IsPrivate, current.IsPrivate);
        CompareValue(changes, nameof(CustomApiDefinition.AllowedCustomProcessingStepType), desired.AllowedCustomProcessingStepType, current.AllowedCustomProcessingStepType);
        CompareValue(changes, nameof(CustomApiDefinition.BindingType), desired.BindingType, current.BindingType);
        CompareValue(changes, nameof(CustomApiDefinition.WorkflowSdkStepEnabled), desired.WorkflowSdkStepEnabled, current.WorkflowSdkStepEnabled);
        CompareValue(changes, nameof(CustomApiDefinition.RequestTypeName), desired.RequestTypeName, current.RequestTypeName);
        CompareValue(changes, nameof(CustomApiDefinition.ResponseTypeName), desired.ResponseTypeName, current.ResponseTypeName);
        CompareValue(changes, nameof(CustomApiDefinition.InputParameters), DescribeParameters(desired.InputParameters), DescribeParameters(current.InputParameters));
        CompareValue(changes, nameof(CustomApiDefinition.OutputParameters), DescribeParameters(desired.OutputParameters), DescribeParameters(current.OutputParameters));
        return changes.ToArray();
    }

    private static void CompareValue<T>(List<CustomApiPropertyChange> changes, string propertyName, T desired, T current)
    {
        if (!EqualityComparer<T>.Default.Equals(desired, current))
        {
            changes.Add(new CustomApiPropertyChange
            {
                PropertyName = propertyName,
                Before = current?.ToString(),
                After = desired?.ToString()
            });
        }
    }

    private static void CompareValue(List<CustomApiPropertyChange> changes, string propertyName, string? desired, string? current)
    {
        if (!string.Equals(desired, current, StringComparison.Ordinal))
        {
            changes.Add(new CustomApiPropertyChange
            {
                PropertyName = propertyName,
                Before = current,
                After = desired
            });
        }
    }

    private static string DescribeParameters(CustomApiParameterDefinition[] parameters)
    {
        return string.Join("|", parameters
            .OrderBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .Select(parameter => $"{parameter.Name}:{parameter.ParameterTypeName}:{parameter.IsOptional}:{parameter.IsCollection}:{parameter.Description}"));
    }
}
