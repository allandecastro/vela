using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vela.Caching;
using Vela.Context;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Localization
{
    /// <summary>
    /// Default <see cref="IMessageService"/>: composes an ordered list of <see cref="IMessageSource"/> providers,
    /// resolves the current user's UI language from <c>usersettings.uilanguageid</c>, and formats templates with
    /// <c>string.Format</c> in the resolved culture. A message missing in the user's language falls back to the
    /// organization's base language (<c>organization.languagecode</c>), then to <see cref="DefaultLanguageCode"/>.
    /// </summary>
    public sealed class MessageService : IMessageService
    {
        private const string CacheKeyPrefix = "vela:userlang:";
        private const string OrgCacheKeyPrefix = "vela:orglang:";

        private readonly ILocalPluginContext _context;
        private readonly IPluginCacheService _cache;
        private readonly List<IMessageSource> _sources = new List<IMessageSource>();
        private int? _userLanguagePerExecution;
        private int? _organizationLanguagePerExecution;

        /// <summary>Creates the service over a plugin context and the shared cache.</summary>
        public MessageService(ILocalPluginContext context, IPluginCacheService cache)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        /// <inheritdoc />
        public int DefaultLanguageCode { get; set; } = 1033;

        /// <inheritdoc />
        public IMessageService AddSource(IMessageSource source)
        {
            _sources.Add(source ?? throw new ArgumentNullException(nameof(source)));
            return this;
        }

        /// <inheritdoc />
        public string GetMessage(string key, params object[] args) =>
            GetMessageIn(ResolveUserLanguageCode(), key, args);

        /// <inheritdoc />
        public string GetMessageIn(int languageCode, string key, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A message key is required.", nameof(key));
            }

            if (TryResolveTemplate(key, languageCode, out string? template))
            {
                return Format(key, template!, languageCode, args);
            }

            throw new InvalidPluginExecutionException(
                $"No message found for key '{key}' in language {languageCode} (or the organization language " +
                $"{ResolveOrganizationLanguageCode()}, or default {DefaultLanguageCode}).");
        }

        /// <inheritdoc />
        public bool TryGetMessage(string key, out string? message, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                message = null;
                return false;
            }

            int languageCode = ResolveUserLanguageCode();
            if (TryResolveTemplate(key, languageCode, out string? template))
            {
                message = Format(key, template!, languageCode, args);
                return true;
            }

            message = null;
            return false;
        }

        /// <inheritdoc />
        public int ResolveUserLanguageCode(TimeSpan? cacheFor = null)
        {
            if (cacheFor.HasValue)
            {
                // Opt-in cross-execution cache, scoped by organization + user (a user's UI language is per user).
                string cacheKey = CacheKeyPrefix + _context.OrganizationId.ToString("N") + ":" +
                    _context.InitiatingUserId.ToString("N");
                return _cache.GetOrCreate(cacheKey, cacheFor.Value, () => QueryUserLanguage(cacheFor));
            }

            return _userLanguagePerExecution ??= QueryUserLanguage(null);
        }

        /// <inheritdoc />
        public int ResolveOrganizationLanguageCode(TimeSpan? cacheFor = null)
        {
            if (cacheFor.HasValue)
            {
                string cacheKey = OrgCacheKeyPrefix + _context.OrganizationId.ToString("N");
                return _cache.GetOrCreate(cacheKey, cacheFor.Value, QueryOrganizationLanguage);
            }

            return _organizationLanguagePerExecution ??= QueryOrganizationLanguage();
        }

        // The organization's base language (organization.languagecode), read through the system service. Zero when
        // it cannot be read, so the chain moves on to DefaultLanguageCode.
        private int QueryOrganizationLanguage()
        {
            QueryExpression query = new QueryExpression("organization")
            {
                ColumnSet = new ColumnSet("languagecode"),
                TopCount = 1,
            };

            Entity? organization = _context.SystemService.RetrieveMultiple(query)?.Entities.FirstOrDefault();
            int languageCode = organization?.GetAttributeValue<int>("languagecode") ?? 0;
            return languageCode > 0 ? languageCode : 0;
        }

        // Reads the initiating user's UI language through the elevated system service; the initiating user is the
        // person who triggered the operation, so a business message is shown in their language even when the plugin
        // runs as another user. Falls back to the organization language, then the default, when there is no setting.
        private int QueryUserLanguage(TimeSpan? cacheFor)
        {
            QueryExpression query = new QueryExpression("usersettings")
            {
                ColumnSet = new ColumnSet("uilanguageid"),
                TopCount = 1,
            };
            query.Criteria.AddCondition("systemuserid", ConditionOperator.Equal, _context.InitiatingUserId);

            Entity? settings = _context.SystemService.RetrieveMultiple(query).Entities.FirstOrDefault();
            int uiLanguage = settings?.GetAttributeValue<int>("uilanguageid") ?? 0;
            if (uiLanguage > 0)
            {
                return uiLanguage;
            }

            int organizationLanguage = ResolveOrganizationLanguageCode(cacheFor);
            return organizationLanguage > 0 ? organizationLanguage : DefaultLanguageCode;
        }

        // First source with a template for the requested language wins; otherwise fall back to the organization's base
        // language (read only when needed), then to the default language.
        private bool TryResolveTemplate(string key, int languageCode, out string? template)
        {
            if (TryResolveIn(key, languageCode, out template))
            {
                return true;
            }

            int organizationLanguage = ResolveOrganizationLanguageCode();
            if (organizationLanguage > 0 && organizationLanguage != languageCode && TryResolveIn(key, organizationLanguage, out template))
            {
                return true;
            }

            if (DefaultLanguageCode != languageCode && DefaultLanguageCode != organizationLanguage && TryResolveIn(key, DefaultLanguageCode, out template))
            {
                return true;
            }

            template = null;
            return false;
        }

        private bool TryResolveIn(string key, int languageCode, out string? template)
        {
            foreach (IMessageSource source in _sources)
            {
                if (source.TryGetTemplate(key, languageCode, out template) && template != null)
                {
                    return true;
                }
            }

            template = null;
            return false;
        }

        // A template whose placeholders do not match the arguments (an index past the last argument, an unbalanced
        // brace) is a defect in the message catalog, not a missing message: it throws even from TryGetMessage, as an
        // InvalidPluginExecutionException naming the key and the language instead of a bare FormatException.
        private static string Format(string key, string template, int languageCode, object[]? args)
        {
            if (args == null || args.Length == 0)
            {
                return template;
            }

            try
            {
                return string.Format(ResolveCulture(languageCode), template, args);
            }
            catch (FormatException ex)
            {
                throw new InvalidPluginExecutionException(
                    $"The message template for key '{key}' in language {languageCode} does not match its {args.Length} argument(s): {ex.Message}",
                    ex);
            }
        }

        // The culture for {0} formatting (numbers/dates). Falls back to the invariant culture for an unknown LCID
        // rather than throwing, so a custom or missing language never breaks message resolution.
        private static CultureInfo ResolveCulture(int languageCode)
        {
            if (languageCode <= 0)
            {
                return CultureInfo.InvariantCulture;
            }

            try
            {
                return CultureInfo.GetCultureInfo(languageCode);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.InvariantCulture;
            }
        }
    }
}
