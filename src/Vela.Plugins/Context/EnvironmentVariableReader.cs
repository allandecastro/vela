using System;
using System.Collections.Generic;
using System.Linq;
using Vela.Caching;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Context
{
    /// <summary>
    /// The Dataverse environment variable value type (<c>environmentvariabledefinition.type</c> option set).
    /// </summary>
    public enum EnvironmentVariableType
    {
        /// <summary>The definition could not be read (missing) so its type is unknown.</summary>
        Unknown = 0,

        /// <summary>Free text.</summary>
        String = 100000000,

        /// <summary>Numeric.</summary>
        Number = 100000001,

        /// <summary>Boolean (stored as text, e.g. <c>true</c>/<c>false</c> or <c>yes</c>/<c>no</c>).</summary>
        Boolean = 100000002,

        /// <summary>JSON document.</summary>
        Json = 100000003,

        /// <summary>Data source (connection reference), stored as JSON.</summary>
        DataSource = 100000004,

        /// <summary>Secret backed by Azure Key Vault. Reading the secret value is not supported: resolving one throws.</summary>
        Secret = 100000005,
    }

    /// <summary>
    /// A resolved snapshot of a Dataverse environment variable: whether the definition exists, its type, and the
    /// effective value (the <c>environmentvariablevalue.value</c> when a value record exists, otherwise the
    /// definition's <c>defaultvalue</c>).
    /// </summary>
    public sealed class EnvironmentVariable
    {
        internal EnvironmentVariable(string schemaName, bool exists, EnvironmentVariableType type, string? value)
        {
            SchemaName = schemaName;
            Exists = exists;
            Type = type;
            Value = value;
        }

        /// <summary>The environment variable schema (unique) name, for example <c>adc_ApiBaseUrl</c>.</summary>
        public string SchemaName { get; }

        /// <summary>True when an <c>environmentvariabledefinition</c> with this schema name exists.</summary>
        public bool Exists { get; }

        /// <summary>The definition's value type. <see cref="EnvironmentVariableType.Unknown"/> when it does not exist.</summary>
        public EnvironmentVariableType Type { get; }

        /// <summary>
        /// The effective value: the value record's value if one exists, else the definition's default value; null
        /// when the definition does not exist or has neither a value record nor a default. An explicit empty
        /// string is a real value (not null).
        /// </summary>
        public string? Value { get; }

        /// <summary>True when there is an effective value (including an explicit empty string).</summary>
        public bool HasValue => Value != null;
    }

    /// <summary>
    /// Resolves Dataverse environment variables (definition + value) for plugin code. Registered per execution, so
    /// it memoizes lookups within a single plugin run; an optional TTL opts into cross-execution caching. Handlers
    /// usually call the <see cref="EnvironmentVariableExtensions"/> methods on the context rather than this service
    /// directly.
    /// </summary>
    public interface IEnvironmentVariableReader
    {
        /// <summary>
        /// Resolves the environment variable with the given schema name. When <paramref name="cacheFor"/> is null
        /// the value is read fresh once per execution (memoized within the run); when set, the value is cached
        /// across executions for that duration. A Secret-type variable throws: its stored value is a Key Vault
        /// reference, not the secret.
        /// </summary>
        EnvironmentVariable Resolve(string schemaName, TimeSpan? cacheFor = null);
    }

    /// <summary>
    /// Default <see cref="IEnvironmentVariableReader"/>: reads <c>environmentvariabledefinition</c> left-joined to
    /// its active <c>environmentvariablevalue</c> through the elevated system service, so lookups do not depend on
    /// the caller's privilege.
    /// </summary>
    public sealed class EnvironmentVariableReader : IEnvironmentVariableReader
    {
        private const string CacheKeyPrefix = "vela:envvar:";

        private readonly ILocalPluginContext _context;
        private readonly IPluginCacheService _cache;
        private readonly Dictionary<string, EnvironmentVariable> _perExecution =
            new Dictionary<string, EnvironmentVariable>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Creates the service over a plugin context and the shared cache.</summary>
        public EnvironmentVariableReader(ILocalPluginContext context, IPluginCacheService cache)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        /// <inheritdoc />
        public EnvironmentVariable Resolve(string schemaName, TimeSpan? cacheFor = null)
        {
            EnvironmentVariable variable = ResolveAnyType(schemaName, cacheFor);

            // A Secret's stored value is a Key Vault reference, not the secret: handing it out would let a caller use
            // the reference as if it were the value. Rejected here so the injected reader and the context extensions
            // behave the same.
            if (variable.Exists && variable.Type == EnvironmentVariableType.Secret)
            {
                throw new InvalidPluginExecutionException(
                    $"Environment variable '{schemaName}' is a Secret; reading secret values is not supported (they resolve through Azure Key Vault).");
            }

            return variable;
        }

        private EnvironmentVariable ResolveAnyType(string schemaName, TimeSpan? cacheFor)
        {
            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException("An environment variable schema name is required.", nameof(schemaName));
            }

            if (cacheFor.HasValue)
            {
                // Opt-in cross-execution cache (hot paths): reuse the resolved value across plugin invocations. The
                // cache is a process-wide singleton, so scope the key by organization id - environment variables are
                // environment-scoped, and this prevents a value bleeding across orgs should a worker be shared.
                string cacheKey = CacheKeyPrefix +
                    _context.PluginExecutionContext.OrganizationId.ToString("N") + ":" + schemaName;
                return _cache.GetOrCreate(cacheKey, cacheFor.Value, () => Query(schemaName));
            }

            // Default: fresh per execution, but memoized so repeated reads in one run hit Dataverse once.
            if (_perExecution.TryGetValue(schemaName, out EnvironmentVariable cached))
            {
                return cached;
            }

            EnvironmentVariable resolved = Query(schemaName);
            _perExecution[schemaName] = resolved;
            return resolved;
        }

        private EnvironmentVariable Query(string schemaName)
        {
            // One round-trip: the definition, left-joined to its active value record. The value column comes back
            // as an AliasedValue under the link alias; a definition with no (active) value yields a null alias, so
            // the effective value falls back to the definition's default.
            QueryExpression query = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("schemaname", "type", "defaultvalue"),
                TopCount = 1,
            };
            query.Criteria.AddCondition("schemaname", ConditionOperator.Equal, schemaName);

            LinkEntity value = new LinkEntity(
                "environmentvariabledefinition",
                "environmentvariablevalue",
                "environmentvariabledefinitionid",
                "environmentvariabledefinitionid",
                JoinOperator.LeftOuter)
            {
                Columns = new ColumnSet("value"),
                EntityAlias = "evv",
            };
            value.LinkCriteria.AddCondition("statecode", ConditionOperator.Equal, 0); // active values only
            query.LinkEntities.Add(value);

            Entity? definition = _context.SystemService.RetrieveMultiple(query).Entities.FirstOrDefault();
            if (definition == null)
            {
                return new EnvironmentVariable(schemaName, exists: false, EnvironmentVariableType.Unknown, value: null);
            }

            int typeCode = definition.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0;
            string? defaultValue = definition.GetAttributeValue<string>("defaultvalue");
            string? valueRecord = definition.GetAttributeValue<AliasedValue>("evv.value")?.Value as string;

            // The value record wins when present (even an explicit empty string); otherwise the default applies.
            string? effective = valueRecord ?? defaultValue;
            return new EnvironmentVariable(schemaName, exists: true, ToType(typeCode), effective);
        }

        private static EnvironmentVariableType ToType(int typeCode) =>
            Enum.IsDefined(typeof(EnvironmentVariableType), typeCode)
                ? (EnvironmentVariableType)typeCode
                : EnvironmentVariableType.Unknown;
    }
}
