namespace Vela.Localization
{
    /// <summary>
    /// A source of localized message templates for the message service. Sources are consulted in registration order;
    /// the first that has a template for the requested key and language wins. Implement this to add a provider (an
    /// in-code dictionary, a Dataverse table, an embedded resource, ...).
    /// </summary>
    public interface IMessageSource
    {
        /// <summary>
        /// Tries to get the raw (unformatted) template for a key in a specific language.
        /// </summary>
        /// <param name="key">The message key, for example <c>ERR_MISSING_FIELD</c>.</param>
        /// <param name="languageCode">The LCID to resolve, for example 1033 (English) or 1036 (French).</param>
        /// <param name="template">The template when found (may contain <c>{0}</c> placeholders); otherwise null.</param>
        /// <returns>True when a template exists for the key in that language.</returns>
        bool TryGetTemplate(string key, int languageCode, out string? template);
    }
}
