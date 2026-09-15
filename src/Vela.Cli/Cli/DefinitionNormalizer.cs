using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>A step pre/post image: an alias and the (order-preserving) attribute list it projects.</summary>
internal readonly record struct StepImage(string Alias, string[] Attributes);

/// <summary>
/// Produces canonical, order-stable JSON shapes for plugin steps and custom APIs so that
/// local definitions and live Dataverse definitions compare equal when semantically identical.
/// </summary>
internal static class DefinitionNormalizer
{
    /// <summary>
    /// The single source of truth for the plugin-step JSON shape that plan/apply diffs field-for-field.
    /// Both the live read (<see cref="DataverseRegistration.GetStepItems"/>) and the local definition
    /// (<see cref="NormalizeStepDefinition"/>) build their shape here, so the two can't diverge by
    /// construction — any drift silently breaks idempotence (the bug class fixed in #68). This owns the
    /// field set, field order and image ordering (by alias, ordinal); callers pass already-coerced
    /// primitive values and the step <paramref name="name"/> (raw from Dataverse on the live side,
    /// computed on the local side — the one field a diff is meant to compare).
    /// </summary>
    public static JsonElement BuildStepShape(
        string pluginTypeName,
        string message,
        string entityLogicalName,
        string stepKey,
        string name,
        int stage,
        int mode,
        int executionOrder,
        string[] filteringAttributes,
        IEnumerable<StepImage> preImages,
        IEnumerable<StepImage> postImages,
        bool enabled,
        string unsecureConfig,
        string secureConfig,
        JsonSerializerOptions jsonOptions)
    {
        // Global (entity-agnostic) messages such as Associate/Disassociate cannot carry a primary entity on
        // their sdkmessagefilter (primaryobjecttypecode is "none"), so the live read always comes back with an
        // empty entityLogicalName. A local declaration of "account" would therefore diverge on every diff and
        // the step would never converge (perpetual "changed"). Neutralize the field for these messages so both
        // sides compare equal; the entity is still reflected in the step name, which matches on both sides.
        if (IsGlobalMessage(message))
        {
            entityLogicalName = string.Empty;
        }

        var shape = new
        {
            pluginTypeName,
            message,
            entityLogicalName,
            stepKey,
            name,
            stage,
            mode,
            executionOrder,
            filteringAttributes,
            preImages = OrderImages(preImages),
            postImages = OrderImages(postImages),
            enabled,
            unsecureConfig,
            // Secure config can hold secrets (API keys, connection strings). It must never appear in the
            // plan/inspect JSON output, but the diff still has to detect when it changes — so the shape carries
            // a stable hash, not the value. Same value on both sides -> same hash -> no diff; a changed secret
            // -> different hash -> "changed" (without revealing either value). The real value is written by the
            // apply path via StepApplyDefinition.SecureConfig, which is unaffected by this.
            secureConfig = HashSecret(secureConfig)
        };

        return JsonSerializer.SerializeToElement(shape, jsonOptions);
    }

    // Messages that are not bound to a primary entity via the sdkmessagefilter (its primaryobjecttypecode is
    // "none"). Steps on these read back with an empty entityLogicalName regardless of what was declared, so the
    // field must be excluded from the diff to keep plan/apply idempotent.
    internal static bool IsGlobalMessage(string message) =>
        string.Equals(message, "Associate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(message, "Disassociate", StringComparison.OrdinalIgnoreCase);

    // Redacts a secret to a stable digest so it never lands in plan output while staying change-detectable.
    // Empty stays empty (no secret to hide), so "no secure config" reads cleanly rather than as a hash.
    internal const string SecretDigestPrefix = "sha256:";

    internal static string HashSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Idempotent: re-normalizing an already-normalized shape must be a fixed point, so an existing digest
        // passes through unchanged instead of being hashed again.
        if (value.StartsWith(SecretDigestPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return SecretDigestPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static object[] OrderImages(IEnumerable<StepImage> images) =>
        images
            .Select(image => new { alias = image.Alias, attributes = image.Attributes })
            .OrderBy(x => x.alias, StringComparer.Ordinal)
            .ToArray<object>();

    public static string BuildStepLifecycleLabel(int stage, int mode)
    {
        string stageLabel = stage switch
        {
            10 => "PreVal",
            20 => "PreOp",
            30 => "MainOp",
            40 => "PostOp",
            _ => "PostOp"
        };

        string modeLabel = mode switch
        {
            0 => "Sync",
            1 => "Async",
            _ => "Sync"
        };

        return $"{stageLabel}{modeLabel}";
    }

    public static JsonElement NormalizeStepDefinition(JsonElement raw, JsonSerializerOptions jsonOptions)
    {
        string pluginTypeName = TryGetString(raw, "pluginTypeName") ?? string.Empty;
        string message = TryGetString(raw, "message") ?? string.Empty;
        string entityLogicalName = TryGetString(raw, "entityLogicalName") ?? string.Empty;
        string stepKey = TryGetString(raw, "stepKey") ?? string.Empty;
        int mode = TryGetInt(raw, "mode");
        // Apply the same platform coercions the apply engine performs at write time (async -> PostOperation,
        // unknown stage -> default, rank clamp) so the local shape matches the live Dataverse shape and
        // plan/apply stay idempotent.
        int stage = DataverseRegistration.NormalizeRegistrationStage(TryGetInt(raw, "stage"), mode);
        int executionOrder = DataverseRegistration.NormalizeRegistrationRank(TryGetInt(raw, "executionOrder", defaultValue: 1));
        string[] filteringAttributes = TryGetStringArray(raw, "filteringAttributes");
        StepImage[] preImages = TryGetStepImages(raw, "preImages");
        StepImage[] postImages = TryGetStepImages(raw, "postImages");
        bool enabled = TryGetBool(raw, "enabled", defaultValue: true);
        string unsecureConfig = TryGetString(raw, "unsecureConfig") ?? string.Empty;
        string secureConfig = TryGetString(raw, "secureConfig") ?? string.Empty;

        string lifecycle = BuildStepLifecycleLabel(stage, mode);
        string name = !string.IsNullOrWhiteSpace(stepKey)
            ? $"{pluginTypeName} - {lifecycle}: {stepKey}"
            : string.IsNullOrWhiteSpace(entityLogicalName)
                ? $"{pluginTypeName} - {lifecycle}: {message}"
                : $"{pluginTypeName} - {lifecycle}: {message} of {entityLogicalName}";

        return BuildStepShape(
            pluginTypeName,
            message,
            entityLogicalName,
            stepKey,
            name,
            stage,
            mode,
            executionOrder,
            filteringAttributes,
            preImages,
            postImages,
            enabled,
            unsecureConfig,
            secureConfig,
            jsonOptions);
    }

    public static JsonElement NormalizeCustomApiDefinition(JsonElement raw, JsonSerializerOptions jsonOptions)
    {
        string pluginTypeName = TryGetString(raw, "pluginTypeName")
            ?? TryGetNestedString(raw, "pluginStep", "pluginTypeName")
            ?? string.Empty;
        string uniqueName = TryGetString(raw, "uniqueName") ?? string.Empty;
        bool isFunction = TryGetBool(raw, "isFunction");
        bool isBound = TryGetBool(raw, "isBound");
        string? boundEntityLogicalName = TryGetString(raw, "boundEntityLogicalName");
        string? executePrivilegeName = TryGetString(raw, "executePrivilegeName");
        bool isPrivate = TryGetBool(raw, "isPrivate");
        int allowedCustomProcessingStepType = TryGetInt(raw, "allowedCustomProcessingStepType");
        int bindingType = TryGetInt(raw, "bindingType", defaultValue: isBound ? 1 : 0);
        bool workflowSdkStepEnabled = TryGetBool(raw, "workflowSdkStepEnabled");
        object[] inputParameters = TryGetCustomApiInputShapes(raw, "inputParameters");
        object[] outputParameters = TryGetCustomApiOutputShapes(raw, "outputParameters");

        object normalized = CustomApiShape.Build(
            pluginTypeName, uniqueName, isFunction, isBound, boundEntityLogicalName, executePrivilegeName,
            isPrivate, allowedCustomProcessingStepType, bindingType, workflowSdkStepEnabled, inputParameters, outputParameters);

        return JsonSerializer.SerializeToElement(normalized, jsonOptions);
    }
}
