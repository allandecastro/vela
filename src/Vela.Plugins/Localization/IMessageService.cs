using System;

namespace Vela.Localization
{
    /// <summary>
    /// Resolves localized messages for plugin code from one or more <see cref="IMessageSource"/> providers, in the
    /// current user's language with a fallback to a default language, and formats them with <c>string.Format</c>.
    /// Registered per execution: it memoizes the resolved user language within a run. Handlers usually reach it
    /// through the context extensions (<c>ctx.GetMessage(...)</c>, <c>ctx.BusinessError(...)</c>) rather than this
    /// service directly.
    /// </summary>
    public interface IMessageService
    {
        /// <summary>
        /// The language used when a message has no template in the resolved user language. Defaults to 1033 (English).
        /// </summary>
        int DefaultLanguageCode { get; set; }

        /// <summary>Adds a message source. Sources are consulted in the order they are added.</summary>
        /// <param name="source">The source to add.</param>
        /// <returns>This service, for chaining.</returns>
        IMessageService AddSource(IMessageSource source);

        /// <summary>
        /// Gets the message for the key in the current user's language (falling back to
        /// <see cref="DefaultLanguageCode"/>), formatted with <paramref name="args"/>. Throws when no template exists.
        /// </summary>
        string GetMessage(string key, params object[] args);

        /// <summary>
        /// Gets the message for the key in a specific language (falling back to <see cref="DefaultLanguageCode"/>),
        /// formatted with <paramref name="args"/>. Throws when no template exists.
        /// </summary>
        string GetMessageIn(int languageCode, string key, params object[] args);

        /// <summary>
        /// Tries to get the message for the key in the current user's language (falling back to
        /// <see cref="DefaultLanguageCode"/>). Returns false when no template exists.
        /// </summary>
        bool TryGetMessage(string key, out string? message, params object[] args);

        /// <summary>
        /// Resolves the current (initiating) user's UI language (<c>usersettings.uilanguageid</c>), falling back to
        /// <see cref="DefaultLanguageCode"/> when it cannot be read.
        /// </summary>
        /// <param name="cacheFor">Optional cross-execution cache duration; null memoizes for the execution only.</param>
        int ResolveUserLanguageCode(TimeSpan? cacheFor = null);
    }
}
