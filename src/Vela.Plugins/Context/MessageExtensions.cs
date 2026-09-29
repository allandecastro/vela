using System;
using Vela.Localization;
using Vela.Plugin;

namespace Vela.Context
{
    /// <summary>
    /// Localized-message helpers on the plugin context, backed by the registered <see cref="IMessageService"/>. Add
    /// sources (an in-code <see cref="DictionaryMessageSource"/>, a <see cref="DataverseTableMessageSource"/>, ...) in
    /// a plugin's <c>ConfigureServices</c> override, then resolve messages here:
    /// <code>
    /// protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context) =>
    ///     context.GetRequiredService&lt;IMessageService&gt;()
    ///         .AddSource(new DictionaryMessageSource().Add(1036, "ERR_X", "Le champ {0} est requis."));
    ///
    /// // in the handler:
    /// throw context.BusinessError("ERR_X", "name");
    /// </code>
    /// </summary>
    public static class MessageExtensions
    {
        /// <summary>Gets the message for the key in the current user's language, formatted with <paramref name="args"/>.</summary>
        public static string GetMessage(this ILocalPluginContext context, string key, params object[] args) =>
            Service(context).GetMessage(key, args);

        /// <summary>Gets the message for the key in a specific language, formatted with <paramref name="args"/>.</summary>
        public static string GetMessageIn(this ILocalPluginContext context, int languageCode, string key, params object[] args) =>
            Service(context).GetMessageIn(languageCode, key, args);

        /// <summary>Tries to get the message for the key in the current user's language; returns false when absent.</summary>
        public static bool TryGetMessage(this ILocalPluginContext context, string key, out string? message, params object[] args) =>
            Service(context).TryGetMessage(key, out message, args);

        /// <summary>
        /// Builds a <see cref="PluginBusinessException"/> whose message is the localized message for the key, so a
        /// handler can throw a clean, translated business error: <c>throw context.BusinessError("KEY", args)</c>.
        /// </summary>
        public static PluginBusinessException BusinessError(this ILocalPluginContext context, string key, params object[] args) =>
            new PluginBusinessException(Service(context).GetMessage(key, args));

        private static IMessageService Service(ILocalPluginContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.GetRequiredService<IMessageService>();
        }
    }
}
