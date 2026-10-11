using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vela.Cli;

/// <summary>
/// Resolves step secure configurations at plan/apply time. Code only names a secure configuration
/// (<c>.WithSecureConfigFrom("PaymentApiKey")</c>); its value comes from <c>--secure-config NAME=VALUE</c>
/// (<c>NAME=@path</c> reads a file) or the <c>VELA_SECURE_CONFIG_NAME</c> environment variable, so a secret is
/// never compiled into the plugin assembly.
/// <para>
/// The environment owns any secure configuration Vela is not given a value for: such a step's local shape takes the
/// live digest, so the diff reports no change and apply never writes, unlinks or deletes it (whether the step
/// declares no name, or declares one whose value was not supplied).
/// </para>
/// </summary>
internal static class SecureConfigResolver
{
    internal const string EnvironmentVariablePrefix = "VELA_SECURE_CONFIG_";

    /// <summary>
    /// Parses <c>--secure-config</c> specs (<c>NAME=VALUE</c> or <c>NAME=@path</c>). Messages name the
    /// configuration, never its value. An inline <c>NAME=VALUE</c> sits in shell history and the process listing, so
    /// it is honored but reported through <paramref name="warn"/>, as <c>--client-secret</c> is.
    /// </summary>
    internal static Dictionary<string, string> ParseSpecs(IEnumerable<string> specs, Func<string, string> readFile, Action<string>? warn = null)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string spec in specs)
        {
            int eq = spec.IndexOf('=');
            string name = eq < 0 ? spec : spec[..eq];
            if (eq <= 0 || !IsName(name))
            {
                // Never echo the argument: a mistyped flag (a bare secret, a missing '=') would leak it to stderr/CI logs.
                throw new ArgumentException("Invalid --secure-config: expected NAME=VALUE or NAME=@path, where NAME is the name given to WithSecureConfigFrom (letters, digits, '_').");
            }

            string value = spec[(eq + 1)..];
            if (value.StartsWith('@'))
            {
                string path = value[1..];
                try
                {
                    value = readFile(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new FileNotFoundException($"Cannot read the secure configuration '{name}' from '{path}': {ex.Message}", path, ex);
                }
            }
            else
            {
                warn?.Invoke($"Warning: --secure-config {name}=VALUE on the command line is exposed to shell history and process listings. Prefer --secure-config {name}=@file or the {EnvironmentVariablePrefix}{name} environment variable.");
            }

            if (!values.TryAdd(name, value))
            {
                throw new ArgumentException($"--secure-config '{name}' is given more than once.");
            }
        }

        return values;
    }

    /// <summary>
    /// Rewrites each local step's <c>secureConfig</c> digest: the digest of the supplied value when Vela manages
    /// it, otherwise the live step's digest (no diff, never written). Returns the local state with the raw values to
    /// write, keyed by full step key, and a diagnostic for each declared configuration that has no value anywhere.
    /// </summary>
    internal static RegistrationState Resolve(
        RegistrationState local,
        RegistrationState current,
        IReadOnlyDictionary<string, string> suppliedValues,
        Func<string, string?> readEnvironment,
        JsonSerializerOptions jsonOptions)
    {
        IReadOnlyDictionary<string, string> namesByStepKey = local.SecureConfigNameByStepKey ?? new Dictionary<string, string>();

        // The live digest of each local step's live counterpart, paired exactly as the diff pairs them (step key, then
        // legacy identity), so a keyless or re-keyed step keeps its live digest instead of showing a phantom change.
        List<RegistrationItem> localSteps = local.Items.Where(DiffEngine.IsStep).ToList();
        List<RegistrationItem> currentSteps = current.Items.Where(DiffEngine.IsStep).ToList();
        int[] paired = DiffEngine.PairSteps(localSteps, currentSteps);
        Dictionary<RegistrationItem, string> liveDigest = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < localSteps.Count; i++)
        {
            if (paired[i] >= 0)
            {
                liveDigest[localSteps[i]] = JsonReader.TryGetString(currentSteps[paired[i]].Definition, "secureConfig") ?? string.Empty;
            }
        }

        Dictionary<string, string> valuesByStepKey = new(StringComparer.Ordinal);
        List<string> diagnostics = [.. local.Diagnostics];
        List<RegistrationItem> items = [];
        foreach (RegistrationItem item in local.Items)
        {
            if (!DiffEngine.IsStep(item))
            {
                items.Add(item);
                continue;
            }

            string key = DiffEngine.GetSecureConfigKey(item);
            string digest = liveDigest.TryGetValue(item, out string? live) ? live : string.Empty;
            if (namesByStepKey.TryGetValue(key, out string? name))
            {
                string? value = suppliedValues.TryGetValue(name, out string? supplied) ? supplied : readEnvironment(EnvironmentVariablePrefix + name);
                if (value != null)
                {
                    valuesByStepKey[key] = value;
                    digest = DefinitionNormalizer.HashSecret(value);
                }
                else if (digest.Length == 0)
                {
                    diagnostics.Add($"Step '{key}' expects the secure configuration '{name}', but none is set in the environment and no value was given; pass --secure-config {name}=VALUE (or set {EnvironmentVariablePrefix}{name}).");
                }
            }

            items.Add(item with { Definition = WithSecureConfig(item.Definition, digest, jsonOptions) });
        }

        return local with { Items = [.. items], Diagnostics = [.. diagnostics], SecureConfigByStepKey = valuesByStepKey };
    }

    private static JsonElement WithSecureConfig(JsonElement definition, string digest, JsonSerializerOptions jsonOptions)
    {
        JsonObject obj = JsonNode.Parse(definition.GetRawText())!.AsObject();
        obj["secureConfig"] = digest;
        return JsonSerializer.SerializeToElement(obj, jsonOptions);
    }

    private static bool IsName(string name) =>
        name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => c == '_' || char.IsAsciiLetterOrDigit(c));
}
