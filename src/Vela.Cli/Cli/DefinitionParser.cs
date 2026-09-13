using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>
/// Pure translation of the canonical JSON registration definitions into the strongly-typed
/// <c>*ApplyDefinition</c> records consumed by the apply engine. No Dataverse I/O — the sibling of
/// <see cref="DefinitionNormalizer"/> on the read side, kept separate so parsing can be reasoned about
/// and tested without a connection.
/// </summary>
internal static class DefinitionParser
{
    internal static StepApplyDefinition ParseStepDefinition(JsonElement definition)
    {
        ImageApplyDefinition[] preImages = ParseImageDefinitions(definition, "preImages");
        ImageApplyDefinition[] postImages = ParseImageDefinitions(definition, "postImages");
        return new StepApplyDefinition(
            PluginTypeName: TryGetString(definition, "pluginTypeName") ?? string.Empty,
            Message: TryGetString(definition, "message") ?? string.Empty,
            EntityLogicalName: TryGetString(definition, "entityLogicalName") ?? string.Empty,
            StepKey: TryGetString(definition, "stepKey") ?? string.Empty,
            HandlerMethodName: TryGetString(definition, "handlerMethodName") ?? string.Empty,
            Stage: TryGetInt(definition, "stage"),
            Mode: TryGetInt(definition, "mode"),
            ExecutionOrder: TryGetInt(definition, "executionOrder", 1),
            FilteringAttributes: TryGetStringArray(definition, "filteringAttributes"),
            PreImages: preImages,
            PostImages: postImages,
            Enabled: TryGetBool(definition, "enabled", defaultValue: true),
            UnsecureConfig: TryGetString(definition, "unsecureConfig") ?? string.Empty,
            SecureConfig: TryGetString(definition, "secureConfig") ?? string.Empty);
    }

    internal static ImageApplyDefinition[] ParseImageDefinitions(JsonElement definition, string propertyName)
    {
        if (!definition.TryGetProperty(propertyName, out JsonElement images) || images.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<ImageApplyDefinition> result = [];
        foreach (JsonElement image in images.EnumerateArray())
        {
            string alias = TryGetString(image, "alias") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            result.Add(new ImageApplyDefinition(alias, TryGetStringArray(image, "attributes")));
        }

        return result.OrderBy(x => x.Alias, StringComparer.Ordinal).ToArray();
    }

    internal static CustomApiApplyDefinition ParseCustomApiDefinition(JsonElement definition)
    {
        CustomApiParameterApplyDefinition[] input = ParseCustomApiParameters(definition, "inputParameters", true);
        CustomApiParameterApplyDefinition[] output = ParseCustomApiParameters(definition, "outputParameters", false);
        return new CustomApiApplyDefinition(
            PluginTypeName: TryGetString(definition, "pluginTypeName") ?? string.Empty,
            UniqueName: TryGetString(definition, "uniqueName") ?? string.Empty,
            IsFunction: TryGetBool(definition, "isFunction"),
            IsBound: TryGetBool(definition, "isBound"),
            BoundEntityLogicalName: TryGetString(definition, "boundEntityLogicalName"),
            ExecutePrivilegeName: TryGetString(definition, "executePrivilegeName"),
            IsPrivate: TryGetBool(definition, "isPrivate"),
            AllowedCustomProcessingStepType: TryGetInt(definition, "allowedCustomProcessingStepType"),
            BindingType: TryGetInt(definition, "bindingType", defaultValue: TryGetBool(definition, "isBound") ? 1 : 0),
            WorkflowSdkStepEnabled: TryGetBool(definition, "workflowSdkStepEnabled"),
            InputParameters: input,
            OutputParameters: output);
    }

    internal static CustomApiParameterApplyDefinition[] ParseCustomApiParameters(JsonElement definition, string propertyName, bool includeOptional)
    {
        if (!definition.TryGetProperty(propertyName, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<CustomApiParameterApplyDefinition> result = [];
        foreach (JsonElement row in values.EnumerateArray())
        {
            string name = TryGetString(row, "name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            bool isOptional = includeOptional && TryGetBool(row, "isOptional");
            string? parameterTypeName = TryGetString(row, "parameterTypeName") ?? TryGetString(row, "typeName");
            bool isCollection = TryGetBool(row, "isCollection");
            string? logicalEntityName = TryGetString(row, "logicalEntityName");
            result.Add(new CustomApiParameterApplyDefinition(name, isOptional, parameterTypeName, isCollection, logicalEntityName));
        }

        return result.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    // A stable, machine-parseable marker for the step key, embedded in the step's description. Unlike the display
    // name, the description is not shown/edited in the plugin registration UI's step row, so the key survives an
    // admin renaming the step or the plugin type (B4). Reading keys off this marker keeps the step's identity
    // stable, so apply updates the step in place instead of delete+recreating it (which loses its id/history).
    internal const string StepKeyMarkerPrefix = "[vela:stepkey=";
    private const string StepKeyMarkerSuffix = "]";

    /// <summary>The stable step-key marker to embed in a step's description.</summary>
    internal static string StepKeyMarker(string stepKey) => $"{StepKeyMarkerPrefix}{stepKey}{StepKeyMarkerSuffix}";

    /// <summary>Extracts the step key from the stable marker in <paramref name="text"/>, or null when absent.</summary>
    internal static string? ExtractStepKeyFromMarker(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        int start = text!.IndexOf(StepKeyMarkerPrefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += StepKeyMarkerPrefix.Length;
        int end = text.IndexOf(StepKeyMarkerSuffix, start, StringComparison.Ordinal);
        return end < 0 ? null : text.Substring(start, end - start);
    }

    /// <summary>
    /// Resolves a live step's key from the stable description marker first (rename-proof), falling back to parsing
    /// the display name for steps written before the marker existed (graceful migration).
    /// </summary>
    internal static string ResolveStepKey(string? description, string? stepName, string pluginTypeName) =>
        ExtractStepKeyFromMarker(description) ?? ParseStepKey(stepName, pluginTypeName);

    internal static string ParseStepKey(string? stepName, string pluginTypeName)
    {
        if (string.IsNullOrWhiteSpace(stepName))
        {
            return string.Empty;
        }

        string value = stepName.Trim();

        // New format: "{pluginTypeName} - {stage}{mode}: {stepKey}"
        string newPrefix = $"{pluginTypeName} - ";
        if (value.StartsWith(newPrefix, StringComparison.Ordinal))
        {
            int stepKeyIndex = value.LastIndexOf(": ", StringComparison.Ordinal);
            if (stepKeyIndex >= 0 && stepKeyIndex + 2 < value.Length)
            {
                return value.Substring(stepKeyIndex + 2);
            }
        }

        // Legacy backward-compat: "df:{pluginTypeName}:{stepKey}"
        string legacyPrefix = $"df:{pluginTypeName}:";
        if (value.StartsWith(legacyPrefix, StringComparison.Ordinal))
        {
            return value.Substring(legacyPrefix.Length);
        }

        return string.Empty;
    }
}
