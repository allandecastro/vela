using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vela.Localization
{
    /// <summary>
    /// An in-code <see cref="IMessageSource"/>: message templates declared directly in the plugin, keyed by language
    /// code and message key. Useful for a fixed catalogue that ships with the assembly and needs no Dataverse round
    /// trip. Build it fluently:
    /// <code>
    /// var source = new DictionaryMessageSource()
    ///     .Add(1033, "ERR_MISSING_FIELD", "The field {0} is required.")
    ///     .Add(1036, "ERR_MISSING_FIELD", "Le champ {0} est obligatoire.");
    /// </code>
    /// </summary>
    public sealed class DictionaryMessageSource : IMessageSource
    {
        // Composite key (language + message key) to a template. Keys are case sensitive (Ordinal): message keys are
        // developer-defined constants, not user input.
        private readonly Dictionary<string, string> _templates = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Adds (or replaces) a single template for a key in a language.</summary>
        /// <param name="languageCode">The LCID, for example 1033 or 1036.</param>
        /// <param name="key">The message key.</param>
        /// <param name="template">The template (may contain <c>{0}</c> placeholders).</param>
        /// <returns>This source, for chaining.</returns>
        public DictionaryMessageSource Add(int languageCode, string key, string template)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A message key is required.", nameof(key));
            }

            _templates[Compose(languageCode, key)] = template ?? throw new ArgumentNullException(nameof(template));
            return this;
        }

        /// <summary>Adds (or replaces) every key in a language from a map of key to template.</summary>
        /// <param name="languageCode">The LCID the templates belong to.</param>
        /// <param name="templates">A map of message key to template.</param>
        /// <returns>This source, for chaining.</returns>
        public DictionaryMessageSource Add(int languageCode, IDictionary<string, string> templates)
        {
            if (templates == null)
            {
                throw new ArgumentNullException(nameof(templates));
            }

            foreach (KeyValuePair<string, string> entry in templates)
            {
                Add(languageCode, entry.Key, entry.Value);
            }

            return this;
        }

        /// <inheritdoc />
        public bool TryGetTemplate(string key, int languageCode, out string? template)
        {
            if (string.IsNullOrEmpty(key))
            {
                template = null;
                return false;
            }

            return _templates.TryGetValue(Compose(languageCode, key), out template);
        }

        private static string Compose(int languageCode, string key) =>
            languageCode.ToString(CultureInfo.InvariantCulture) + ":" + key;
    }
}
