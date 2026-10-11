using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>
/// A step pre/post image: an alias, the (order-preserving) attribute list it projects, and the request property it is
/// taken from (Dataverse <c>messagepropertyname</c>; empty in a raw definition means the message's own).
/// </summary>
internal readonly record struct StepImage(string Alias, string[] Attributes, string MessagePropertyName = "");

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
        string description,
        int stage,
        int mode,
        int executionOrder,
        string[] filteringAttributes,
        IEnumerable<StepImage> preImages,
        IEnumerable<StepImage> postImages,
        bool enabled,
        string unsecureConfig,
        string secureConfigDigest,
        Guid? impersonatingUserId,
        int supportedDeployment,
        JsonSerializerOptions jsonOptions,
        bool asyncAutoDelete = false)
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
            // The step's human-readable description (WithDescription, or the step name when none is declared), without
            // the [vela:stepkey=...] marker apply appends: the live read strips it (DefinitionParser.StripStepKeyMarker).
            description,
            stage,
            mode,
            executionOrder,
            filteringAttributes,
            preImages = OrderImages(preImages),
            postImages = OrderImages(postImages),
            enabled,
            unsecureConfig,
            // Impersonation (Dataverse impersonatinguserid): a specific systemuser, or empty when the step runs as
            // the calling user. Normalized to a canonical lowercase Guid string (empty = none) so the live-read side
            // and the code side compare equal and the step converges instead of showing as perpetually "changed".
            impersonatingUserId = impersonatingUserId is { } uid && uid != Guid.Empty ? uid.ToString() : string.Empty,
            // Supported deployment (Dataverse supporteddeployment): 0 ServerOnly (default), 1 OfflineOnly, 2 Both.
            // Carried so .WithSupportedDeployment(...) is honoured and drift is detected instead of silently ignored.
            supportedDeployment,
            // Dataverse asyncautodelete: meaningful on an asynchronous step only, so a sync step always reads false.
            asyncAutoDelete = mode == 1 && asyncAutoDelete,
            // Secure config can hold secrets (API keys, connection strings). It must never appear in the
            // plan/inspect JSON output, but the diff still has to detect when it changes - so the shape carries
            // a digest, not the value. Same value on both sides -> same digest -> no diff; a changed secret
            // -> different digest -> "changed" (without revealing either value). Apply writes the raw value
            // from RegistrationState.SecureConfigByStepKey (see SecureConfigResolver).
            secureConfig = secureConfigDigest
        };

        return JsonSerializer.SerializeToElement(shape, jsonOptions);
    }

    // Messages that are not bound to a primary entity via the sdkmessagefilter (its primaryobjecttypecode is
    // "none"). Steps on these read back with an empty entityLogicalName regardless of what was declared, so the
    // field must be excluded from the diff to keep plan/apply idempotent.
    // The list is shared with the runtime and the test harness (Vela.Constants.PluginMessages, linked as source).
    internal static bool IsGlobalMessage(string message) => Vela.Constants.PluginMessages.IsGlobalMessage(message);

    // Redacts a secret to a digest so it never lands in plan output while staying change-detectable. The digest is
    // an HMAC keyed per process: the local and live sides of one run compare equal, but a digest in a saved plan
    // cannot be brute-forced offline (a plain SHA-256 of a short or guessable secret could). Empty stays empty (no
    // secret to hide), so "no secure config" reads cleanly rather than as a digest.
    internal const string SecretDigestPrefix = "hmac-sha256:";

    private static readonly byte[] SecretDigestKey = RandomNumberGenerator.GetBytes(32);

    // A digest this process produced: the prefix followed by exactly 64 lowercase hex characters.
    internal static bool IsSecretDigest(string value) =>
        value.Length == SecretDigestPrefix.Length + 64
        && value.StartsWith(SecretDigestPrefix, StringComparison.Ordinal)
        && value.Skip(SecretDigestPrefix.Length).All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    internal static string HashSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Always hash a raw value, even one that happens to start with the digest prefix: passing such a value
        // through would print a secret verbatim in plan/inspect output.
        byte[] hash = HMACSHA256.HashData(SecretDigestKey, Encoding.UTF8.GetBytes(value));
        return SecretDigestPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static object[] OrderImages(IEnumerable<StepImage> images) =>
        images
            .Select(image => new { alias = image.Alias, attributes = image.Attributes, messagePropertyName = image.MessagePropertyName })
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

    /// <summary>
    /// The display name Vela gives a step that has no explicit step key: <c>{plugin} - {lifecycle}: {message}</c>,
    /// with <c> of {entity}</c> appended for an entity-scoped step. Shared by the write side (BuildStepName) and the
    /// read side (ResolveStepKey) so a keyless step's name is recognised as keyless rather than parsed into a
    /// spurious step key that would churn the diff forever (#305).
    /// </summary>
    // Attribute logical names are lower case in Dataverse; canonicalize a code-declared set to lower case and re-sort
    // (Ordinal is case-sensitive) so it matches the live read and does not churn the diff (#305).
    private static string[] LowerSorted(string[] values) =>
        values.Select(v => v.ToLowerInvariant()).OrderBy(v => v, StringComparer.Ordinal).ToArray();

    // Also resolves each image's request property the way apply registers it (the message's own unless the step chose
    // one, e.g. SubordinateId on Merge), so the shape matches the live messagepropertyname and a change of property is
    // a diff.
    private static StepImage[] NormalizeImages(StepImage[] images, string message) =>
        images.Select(i => new StepImage(
            i.Alias,
            LowerSorted(i.Attributes),
            Vela.Steps.StepImageRules.ResolveMessagePropertyName(message, i.MessagePropertyName) ?? string.Empty)).ToArray();

    public static string BuildKeylessStepName(string pluginTypeName, int stage, int mode, string message, string entityLogicalName)
    {
        string lifecycle = BuildStepLifecycleLabel(stage, mode);
        string entity = string.IsNullOrWhiteSpace(entityLogicalName) ? string.Empty : $" of {entityLogicalName}";
        return $"{pluginTypeName} - {lifecycle}: {message}{entity}";
    }

    public static JsonElement NormalizeStepDefinition(JsonElement raw, JsonSerializerOptions jsonOptions)
    {
        string pluginTypeName = TryGetString(raw, "pluginTypeName") ?? string.Empty;
        string message = TryGetString(raw, "message") ?? string.Empty;
        // Dataverse stores entity and attribute logical names in lower case, so the live read is always lower case.
        // Canonicalize the code-declared side to match, or a mixed-case declaration (e.g. "Account", "Name") would
        // diverge from the live read and churn the diff as "changed" on every plan/apply (#305).
        string entityLogicalName = (TryGetString(raw, "entityLogicalName") ?? string.Empty).ToLowerInvariant();
        string stepKey = TryGetString(raw, "stepKey") ?? string.Empty;
        int mode = TryGetInt(raw, "mode");
        // Apply the same platform coercions the apply engine performs at write time (async -> PostOperation,
        // unknown stage -> default, rank clamp) so the local shape matches the live Dataverse shape and
        // plan/apply stay idempotent.
        int stage = DataverseRegistration.NormalizeRegistrationStage(TryGetInt(raw, "stage"), mode);
        int executionOrder = DataverseRegistration.NormalizeRegistrationRank(TryGetInt(raw, "executionOrder", defaultValue: 1));
        string[] filteringAttributes = LowerSorted(TryGetStringArray(raw, "filteringAttributes"));
        StepImage[] preImages = NormalizeImages(TryGetStepImages(raw, "preImages"), message);
        StepImage[] postImages = NormalizeImages(TryGetStepImages(raw, "postImages"), message);
        bool enabled = TryGetBool(raw, "enabled", defaultValue: true);
        string unsecureConfig = TryGetString(raw, "unsecureConfig") ?? string.Empty;
        // A definition carries no secret: code only names its secure configuration, and SecureConfigResolver sets the
        // digest afterwards. The only value kept here is an existing digest, so re-normalizing an already-normalized
        // shape is a fixed point; anything else (a literal from an older package) is never trusted into the shape.
        string rawSecureConfig = TryGetString(raw, "secureConfig") ?? string.Empty;
        string secureConfig = IsSecretDigest(rawSecureConfig) ? rawSecureConfig : string.Empty;
        // StepDefinition.ImpersonatingUserId serializes as a Guid string (or null); parse leniently so an absent or
        // empty value normalizes to "no impersonation" (Guid.Empty) rather than churning the diff.
        Guid? impersonatingUserId = Guid.TryParse(TryGetString(raw, "impersonatingUserId"), out Guid parsed) ? parsed : null;
        int supportedDeployment = TryGetInt(raw, "supportedDeployment");

        string name = !string.IsNullOrWhiteSpace(stepKey)
            ? $"{pluginTypeName} - {BuildStepLifecycleLabel(stage, mode)}: {stepKey}"
            : BuildKeylessStepName(pluginTypeName, stage, mode, message, entityLogicalName);
        // Apply writes the step name when no description is declared (DataverseRegistration.BuildStepDescription), so
        // that is the expected live value; it is also what every step deployed before descriptions were written holds.
        string description = (TryGetString(raw, "description") ?? string.Empty).Trim();
        if (description.Length == 0)
        {
            description = name;
        }

        return BuildStepShape(
            pluginTypeName,
            message,
            entityLogicalName,
            stepKey,
            name,
            description,
            stage,
            mode,
            executionOrder,
            filteringAttributes,
            preImages,
            postImages,
            enabled,
            unsecureConfig,
            secureConfig,
            impersonatingUserId,
            supportedDeployment,
            jsonOptions,
            TryGetBool(raw, "asyncAutoDelete"));
    }

    public static JsonElement NormalizeCustomApiDefinition(JsonElement raw, JsonSerializerOptions jsonOptions)
    {
        string pluginTypeName = TryGetString(raw, "pluginTypeName")
            ?? TryGetNestedString(raw, "pluginStep", "pluginTypeName")
            ?? string.Empty;
        string uniqueName = TryGetString(raw, "uniqueName") ?? string.Empty;
        // Display name and description default to the unique name: what apply writes when none is declared, and what
        // every Custom API deployed before they were written holds.
        string displayName = CustomApiShape.LabelOrDefault(TryGetString(raw, "displayName"), uniqueName);
        string description = CustomApiShape.LabelOrDefault(TryGetString(raw, "description"), uniqueName);
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
            pluginTypeName, uniqueName, displayName, description, isFunction, isBound, boundEntityLogicalName, executePrivilegeName,
            isPrivate, allowedCustomProcessingStepType, bindingType, workflowSdkStepEnabled, inputParameters, outputParameters);

        return JsonSerializer.SerializeToElement(normalized, jsonOptions);
    }
}
