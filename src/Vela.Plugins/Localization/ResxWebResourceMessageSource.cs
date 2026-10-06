using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Vela.Caching;
using Vela.Context;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Localization
{
    /// <summary>
    /// An <see cref="IMessageSource"/> backed by Dataverse "String (RESX)" web resources, following the platform
    /// naming convention <c>&lt;baseName&gt;.&lt;LCID&gt;.resx</c> (for example <c>acme_/strings/messages.1033.resx</c>
    /// for English, <c>.1036.resx</c> for French) - the same convention the model-driven client uses with
    /// <c>Xrm.Utility.getResourceString</c>. A plugin can therefore reuse the exact resx strings an app already ships
    /// client-side as a single source of truth.
    /// <para>
    /// For a requested language the matching web resource row is read once through the elevated system service, its
    /// base64 <c>content</c> decoded and the resx XML parsed into key -&gt; value. The map is memoized for the
    /// execution; pass <c>cacheFor</c> to reuse it across executions (mirroring the labels-table and environment
    /// variable caches). Base-language fallback is the message service's job (it retries the default language across
    /// every source), so this source only ever loads the exact <c>&lt;baseName&gt;.&lt;LCID&gt;.resx</c> asked for; a
    /// missing or malformed resource yields an empty map so resolution falls through rather than failing.
    /// </para>
    /// </summary>
    public sealed class ResxWebResourceMessageSource : IMessageSource
    {
        private const string CacheKeyPrefix = "vela:resx:";

        private readonly ILocalPluginContext _context;
        private readonly IPluginCacheService _cache;
        private readonly string _baseName;
        private readonly TimeSpan? _cacheFor;
        private Dictionary<int, Dictionary<string, string>>? _perExecution;

        /// <summary>Creates the source over a plugin context and the shared cache.</summary>
        /// <param name="context">The plugin context (used for the elevated read and organization scoping).</param>
        /// <param name="cache">The shared plugin cache.</param>
        /// <param name="baseName">The web resource base name without the language suffix, for example
        /// <c>acme_/strings/messages</c>; the per-language resource is <c>&lt;baseName&gt;.&lt;LCID&gt;.resx</c>.</param>
        /// <param name="cacheFor">Optional cross-execution cache duration; null memoizes for the execution only.</param>
        public ResxWebResourceMessageSource(
            ILocalPluginContext context,
            IPluginCacheService cache,
            string baseName,
            TimeSpan? cacheFor = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _baseName = string.IsNullOrWhiteSpace(baseName)
                ? throw new ArgumentException("'baseName' is required.", nameof(baseName))
                : baseName;
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

            return Snapshot(languageCode).TryGetValue(key, out template) && template != null;
        }

        private Dictionary<string, string> Snapshot(int languageCode)
        {
            if (_cacheFor.HasValue)
            {
                // Opt-in cross-execution cache. The cache is a process-wide singleton, so scope the key by
                // organization id (and the base name + language) to prevent strings bleeding across orgs.
                string cacheKey = CacheKeyPrefix + _context.OrganizationId.ToString("N") + ":" + _baseName + ":" +
                    languageCode.ToString(CultureInfo.InvariantCulture);
                return _cache.GetOrCreate(cacheKey, _cacheFor.Value, () => Load(languageCode));
            }

            // Default: read once per language, memoized for the execution.
            _perExecution ??= new Dictionary<int, Dictionary<string, string>>();
            if (!_perExecution.TryGetValue(languageCode, out Dictionary<string, string>? map))
            {
                map = Load(languageCode);
                _perExecution[languageCode] = map;
            }

            return map;
        }

        // The web resource name for a language follows the platform convention <baseName>.<LCID>.resx. An absent or
        // unreadable resource yields an empty map, so the message service falls through to the next source / the
        // default language rather than failing.
        private Dictionary<string, string> Load(int languageCode)
        {
            string name = _baseName + "." + languageCode.ToString(CultureInfo.InvariantCulture) + ".resx";
            string? base64 = RetrieveContent(name);
            if (string.IsNullOrEmpty(base64))
            {
                return Empty();
            }

            try
            {
                string xml = Encoding.UTF8.GetString(Convert.FromBase64String(base64!));
                return Parse(xml);
            }
            catch (Exception ex) when (ex is FormatException || ex is XmlException)
            {
                // A corrupt resource must not break message resolution; trace it so the author can find the bad
                // file, and fall through (empty map) to the next source / default language.
                _context.Trace("Vela: could not read RESX web resource '{0}': {1}", name, ex.Message);
                return Empty();
            }
        }

        private string? RetrieveContent(string webResourceName)
        {
            IOrganizationService service = _context.AsSystem();
            QueryExpression query = new QueryExpression("webresource")
            {
                ColumnSet = new ColumnSet("content"),
                TopCount = 1,
            };
            query.Criteria.AddCondition("name", ConditionOperator.Equal, webResourceName);

            Entity? row = service.RetrieveMultiple(query).Entities.FirstOrDefault();
            return row?.GetAttributeValue<string>("content");
        }

        // Parses the resx XML into key -> value. A resx data element is <data name="KEY"><value>TEXT</value></data>;
        // a data element carrying a type or mimetype is a typed/binary resource (an image, an icon, ...) and is
        // skipped, so only plain string entries become message templates.
        private static Dictionary<string, string> Parse(string resxXml)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            XElement? root = XDocument.Parse(resxXml).Root;
            if (root == null)
            {
                return map;
            }

            foreach (XElement data in root.Elements("data"))
            {
                if (data.Attribute("type") != null || data.Attribute("mimetype") != null)
                {
                    continue;
                }

                string? name = data.Attribute("name")?.Value;
                XElement? value = data.Element("value");
                if (string.IsNullOrEmpty(name) || value == null)
                {
                    continue;
                }

                map[name!] = value.Value;
            }

            return map;
        }

        private static Dictionary<string, string> Empty() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
