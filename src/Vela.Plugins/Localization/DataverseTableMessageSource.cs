using System;
using System.Collections.Generic;
using System.Globalization;
using Vela.Caching;
using Vela.Context;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Localization
{
    /// <summary>
    /// An <see cref="IMessageSource"/> backed by a Dataverse "labels" table: rows carrying a message key, a language
    /// code and a template. The table schema is configurable (its logical name and the key / language / value column
    /// names) so it fits any existing labels table.
    /// <para>
    /// The whole table is read once and cached as an in-memory snapshot, so a plugin run does not query per message.
    /// By default the snapshot is memoized for the execution; pass <c>cacheFor</c> to reuse it across executions for
    /// that duration (mirroring the environment-variable cache). The table is read through the elevated system
    /// service, so lookups do not depend on the calling user's privilege.
    /// </para>
    /// </summary>
    public sealed class DataverseTableMessageSource : IMessageSource
    {
        private const string CacheKeyPrefix = "vela:labels:";

        private readonly ILocalPluginContext _context;
        private readonly IPluginCacheService _cache;
        private readonly string _table;
        private readonly string _keyColumn;
        private readonly string _languageColumn;
        private readonly string _valueColumn;
        private readonly TimeSpan? _cacheFor;
        private Dictionary<string, string>? _perExecution;

        /// <summary>Creates the source over a plugin context and the shared cache.</summary>
        /// <param name="context">The plugin context (used for the elevated read and organization scoping).</param>
        /// <param name="cache">The shared plugin cache.</param>
        /// <param name="tableLogicalName">The labels table logical name, for example <c>vela_label</c>.</param>
        /// <param name="keyColumn">The column holding the message key.</param>
        /// <param name="languageColumn">The column holding the LCID (a whole number or choice).</param>
        /// <param name="valueColumn">The column holding the template.</param>
        /// <param name="cacheFor">Optional cross-execution cache duration; null memoizes for the execution only.</param>
        public DataverseTableMessageSource(
            ILocalPluginContext context,
            IPluginCacheService cache,
            string tableLogicalName,
            string keyColumn,
            string languageColumn,
            string valueColumn,
            TimeSpan? cacheFor = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _table = Require(tableLogicalName, nameof(tableLogicalName));
            _keyColumn = Require(keyColumn, nameof(keyColumn));
            _languageColumn = Require(languageColumn, nameof(languageColumn));
            _valueColumn = Require(valueColumn, nameof(valueColumn));
            _cacheFor = cacheFor;
        }

        /// <inheritdoc />
        public bool TryGetTemplate(string key, int languageCode, out string? template)
        {
            if (string.IsNullOrEmpty(key))
            {
                template = null;
                return false;
            }

            return Snapshot().TryGetValue(Compose(languageCode, key), out template);
        }

        private Dictionary<string, string> Snapshot()
        {
            if (_cacheFor.HasValue)
            {
                // Opt-in cross-execution cache. The cache is a process-wide singleton, so scope the key by
                // organization id (and the table) to prevent labels bleeding across orgs on a shared worker.
                string cacheKey = CacheKeyPrefix + _context.OrganizationId.ToString("N") + ":" + _table;
                return _cache.GetOrCreate(cacheKey, _cacheFor.Value, Load);
            }

            // Default: read once, memoized for the execution.
            return _perExecution ??= Load();
        }

        private Dictionary<string, string> Load()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            IOrganizationService service = _context.AsSystem();
            QueryExpression query = new QueryExpression(_table)
            {
                ColumnSet = new ColumnSet(_keyColumn, _languageColumn, _valueColumn),
                PageInfo = new PagingInfo { PageNumber = 1, Count = 5000 },
            };

            while (true)
            {
                EntityCollection page = service.RetrieveMultiple(query);
                foreach (Entity row in page.Entities)
                {
                    string? key = row.GetAttributeValue<string>(_keyColumn);
                    string? value = row.GetAttributeValue<string>(_valueColumn);
                    if (string.IsNullOrEmpty(key) || value == null)
                    {
                        continue;
                    }

                    map[Compose(ReadLanguage(row), key!)] = value;
                }

                if (!page.MoreRecords)
                {
                    break;
                }

                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }

            return map;
        }

        // The language column may be a whole number or a choice; read either shape into an LCID.
        private int ReadLanguage(Entity row)
        {
            if (!row.Contains(_languageColumn))
            {
                return 0;
            }

            switch (row[_languageColumn])
            {
                case OptionSetValue optionSet:
                    return optionSet.Value;
                case int number:
                    return number;
                case long large:
                    return (int)large;
                case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                    return parsed;
                default:
                    return 0;
            }
        }

        private static string Compose(int languageCode, string key) =>
            languageCode.ToString(CultureInfo.InvariantCulture) + ":" + key;

        private static string Require(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"'{name}' is required.", name) : value;
    }
}
