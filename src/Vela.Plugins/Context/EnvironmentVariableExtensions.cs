using System;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;

namespace Vela.Context
{
    /// <summary>
    /// Reads Dataverse environment variables from a plugin handler. The effective value is the
    /// <c>environmentvariablevalue.value</c> when a value record exists, otherwise the definition's
    /// <c>defaultvalue</c>.
    /// <para>
    /// Three ways to handle a missing value: <see cref="GetEnvironmentVariable(ILocalPluginContext, string, TimeSpan?)"/>
    /// (and the typed <c>...As&lt;T&gt;</c>) throw when there is no effective value; the <c>fallback</c> overloads
    /// return a caller-supplied default; the <c>TryGet...</c> methods return false. "Missing" means the definition
    /// does not exist, or it has no value record and no default.
    /// </para>
    /// <para>
    /// Caching: values are read fresh once per execution (memoized within the run) by default. Pass
    /// <c>cacheFor</c> to reuse the value across executions for that duration (via the plugin cache) on hot paths.
    /// Secret-type variables are not supported and throw.
    /// </para>
    /// </summary>
    public static class EnvironmentVariableExtensions
    {
        /// <summary>Gets the effective string value; throws when there is no effective value.</summary>
        public static string GetEnvironmentVariable(this ILocalPluginContext context, string schemaName, TimeSpan? cacheFor = null)
        {
            EnvironmentVariable variable = Resolve(context, schemaName, cacheFor);
            return variable.HasValue ? variable.Value! : throw Missing(variable, schemaName);
        }

        /// <summary>Gets the effective string value, or <paramref name="fallback"/> when there is no effective value.</summary>
        public static string GetEnvironmentVariable(this ILocalPluginContext context, string schemaName, string fallback, TimeSpan? cacheFor = null)
        {
            EnvironmentVariable variable = Resolve(context, schemaName, cacheFor);
            return variable.HasValue ? variable.Value! : fallback;
        }

        /// <summary>Tries to get the effective string value; returns false when there is no effective value.</summary>
        public static bool TryGetEnvironmentVariable(this ILocalPluginContext context, string schemaName, out string? value, TimeSpan? cacheFor = null)
        {
            EnvironmentVariable variable = Resolve(context, schemaName, cacheFor);
            value = variable.Value;
            return variable.HasValue;
        }

        /// <summary>
        /// Gets the effective value converted to <typeparamref name="T"/> (primitives/Guid/DateTime via invariant
        /// parsing, complex types via JSON); throws when there is no effective value or the value cannot convert.
        /// </summary>
        public static T GetEnvironmentVariableAs<T>(this ILocalPluginContext context, string schemaName, TimeSpan? cacheFor = null)
        {
            EnvironmentVariable variable = Resolve(context, schemaName, cacheFor);
            return variable.HasValue ? Convert<T>(variable.Value!, schemaName) : throw Missing(variable, schemaName);
        }

        /// <summary>Gets the effective value converted to <typeparamref name="T"/>, or <paramref name="fallback"/> when there is no effective value.</summary>
        public static T GetEnvironmentVariableAs<T>(this ILocalPluginContext context, string schemaName, T fallback, TimeSpan? cacheFor = null)
        {
            EnvironmentVariable variable = Resolve(context, schemaName, cacheFor);
            return variable.HasValue ? Convert<T>(variable.Value!, schemaName) : fallback;
        }

        /// <summary>
        /// Tries to get the effective value converted to <typeparamref name="T"/>; returns false when there is no
        /// effective value OR the value cannot be converted to <typeparamref name="T"/>.
        /// </summary>
        public static bool TryGetEnvironmentVariableAs<T>(this ILocalPluginContext context, string schemaName, out T value, TimeSpan? cacheFor = null)
        {
            EnvironmentVariable variable = Resolve(context, schemaName, cacheFor);
            if (!variable.HasValue)
            {
                value = default!;
                return false;
            }

            try
            {
                value = Convert<T>(variable.Value!, schemaName);
                return true;
            }
            catch
            {
                value = default!;
                return false;
            }
        }

        // Resolves via the per-execution service and rejects Secret-type variables (their value resolves through
        // Azure Key Vault, which this service does not read).
        private static EnvironmentVariable Resolve(ILocalPluginContext context, string schemaName, TimeSpan? cacheFor)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            EnvironmentVariable variable = context.GetRequiredService<IEnvironmentVariableReader>().Resolve(schemaName, cacheFor);
            if (variable.Exists && variable.Type == EnvironmentVariableType.Secret)
            {
                throw new InvalidPluginExecutionException(
                    $"Environment variable '{schemaName}' is a Secret; reading secret values is not supported (they resolve through Azure Key Vault).");
            }

            return variable;
        }

        private static InvalidPluginExecutionException Missing(EnvironmentVariable variable, string schemaName) =>
            new InvalidPluginExecutionException(variable.Exists
                ? $"Environment variable '{schemaName}' has no value and no default value."
                : $"Environment variable '{schemaName}' is not defined in this environment.");

        private static T Convert<T>(string raw, string schemaName)
        {
            Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            try
            {
                if (target == typeof(string))
                {
                    return (T)(object)raw;
                }

                if (target == typeof(bool))
                {
                    return (T)(object)ParseBool(raw);
                }

                if (target == typeof(Guid))
                {
                    return (T)(object)Guid.Parse(raw.Trim());
                }

                if (target == typeof(DateTime))
                {
                    return (T)(object)DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                }

                if (target.IsEnum)
                {
                    return (T)Enum.Parse(target, raw.Trim(), ignoreCase: true);
                }

                if (target.IsPrimitive || target == typeof(decimal))
                {
                    return (T)System.Convert.ChangeType(raw.Trim(), target, CultureInfo.InvariantCulture);
                }

                // Complex type (or a JSON-typed variable): deserialize the value as JSON.
                return JsonConvert.DeserializeObject<T>(raw)!;
            }
            catch (Exception ex) when (!(ex is InvalidPluginExecutionException))
            {
                throw new InvalidPluginExecutionException(
                    $"Environment variable '{schemaName}' value could not be converted to {typeof(T).Name}: {ex.Message}", ex);
            }
        }

        // Dataverse Boolean environment variables are stored as text; accept the common encodings.
        private static bool ParseBool(string raw)
        {
            string trimmed = raw.Trim();
            if (bool.TryParse(trimmed, out bool parsed))
            {
                return parsed;
            }

            switch (trimmed.ToLowerInvariant())
            {
                case "yes":
                case "1":
                    return true;
                case "no":
                case "0":
                    return false;
                default:
                    throw new FormatException($"'{raw}' is not a valid boolean.");
            }
        }
    }
}
