using System;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Vela.Caching;
using Vela.Context;
using Vela.Localization;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Edge behaviour of the runtime services (plugins-services mutation area): the cache's capacity sweep and its error
// messages, and the message service's language fallbacks, source precedence and formatting.
public sealed class RuntimeServicesEdgeTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- PluginCacheService ----------------------------------------------------------------------------------------

    [Fact]
    public void A_cache_of_one_entry_keeps_the_latest_entry()
    {
        PluginCacheService cache = new(maxEntries: 1);

        cache.GetOrCreate("a", TimeSpan.FromMinutes(5), () => 1);
        cache.GetOrCreate("b", TimeSpan.FromMinutes(5), () => 2);

        Assert.Equal(1, cache.MaxEntries);
        Assert.Equal(0, cache.RemoveByPrefix("a"));
        Assert.Equal(1, cache.RemoveByPrefix("b"));
    }

    [Fact]
    public void An_expired_entry_stays_resident_while_the_cache_is_at_capacity()
    {
        // The sweep only runs once an insert pushes the count OVER the cap; at the cap nothing is swept.
        DateTimeOffset now = Start;
        PluginCacheService cache = new(2, () => now);
        cache.GetOrCreate("stale", TimeSpan.FromSeconds(1), () => 1);
        now = now.AddSeconds(5);

        cache.GetOrCreate("live", TimeSpan.FromMinutes(5), () => 2);

        Assert.Equal(1, cache.RemoveByPrefix("stale"));
        Assert.Equal(1, cache.RemoveByPrefix("live"));
    }

    [Fact]
    public void The_sweep_stops_as_soon_as_the_cache_is_back_at_capacity()
    {
        // Two expired entries, one insert over the cap of 2: one expired entry is enough to get back to the cap,
        // so the other stays resident (expiry is lazy) and the new entry is kept.
        DateTimeOffset now = Start;
        PluginCacheService cache = new(2, () => now);
        cache.GetOrCreate("stale:1", TimeSpan.FromSeconds(1), () => 1);
        cache.GetOrCreate("stale:2", TimeSpan.FromSeconds(1), () => 2);
        now = now.AddSeconds(5);

        cache.GetOrCreate("live", TimeSpan.FromMinutes(5), () => 3);

        Assert.Equal(1, cache.RemoveByPrefix("stale:"));
        Assert.Equal(1, cache.RemoveByPrefix("live"));
    }

    [Fact]
    public void Invalidate_rejects_a_blank_key()
    {
        PluginCacheService cache = new();

        Assert.Equal("key", Assert.Throws<ArgumentNullException>(() => cache.Invalidate(" ")).ParamName);
    }

    [Fact]
    public void Invalid_arguments_name_the_rule_they_break()
    {
        Assert.StartsWith("maxEntries must be at least 1.",
            Assert.Throws<ArgumentOutOfRangeException>(() => new PluginCacheService(0)).Message, StringComparison.Ordinal);
        Assert.StartsWith("TTL must be greater than zero.",
            Assert.Throws<ArgumentOutOfRangeException>(() => new PluginCacheService().GetOrCreate("k", TimeSpan.Zero, () => 1)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_an_entry_as_another_type_names_both_types()
    {
        PluginCacheService cache = new();
        cache.GetOrCreate("k", TimeSpan.FromMinutes(1), () => 42);
        cache.GetOrCreate<string?>("n", TimeSpan.FromMinutes(1), () => null);

        InvalidOperationException wrongType = Assert.Throws<InvalidOperationException>(() => cache.GetOrCreate("k", TimeSpan.FromMinutes(1), () => "x"));
        InvalidOperationException nullAsValueType = Assert.Throws<InvalidOperationException>(() => cache.GetOrCreate("n", TimeSpan.FromMinutes(1), () => 1));

        Assert.Equal("Cache key 'k' contains 'System.Int32' but requested 'System.String'.", wrongType.Message);
        Assert.Equal("Cache key 'n' contains 'null' but requested 'System.Int32'.", nullAsValueType.Message);
    }

    // ---- MessageService ------------------------------------------------------------------------------------------

    private static readonly Guid User = Guid.NewGuid();

    private static MessageService Messages(params Entity[] rows)
    {
        InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(rows);
        ILocalPluginContext ctx = new PluginContextBuilder()
            .WithSystemService(crm)
            .WithInitiatingUserId(User)
            .WithOrganizationId(Guid.NewGuid())
            .Build();
        return new MessageService(ctx, new PluginCacheService());
    }

    private static Entity Settings(Guid user, int uiLanguage) =>
        new("usersettings", Guid.NewGuid()) { ["systemuserid"] = user, ["uilanguageid"] = uiLanguage };

    [Fact]
    public void A_blank_key_is_refused_with_a_clear_message()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Messages().GetMessageIn(1033, " "));

        Assert.StartsWith("A message key is required.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("key", ex.ParamName);
    }

    [Fact]
    public void The_language_is_the_initiating_users_not_another_users()
    {
        MessageService svc = Messages(Settings(Guid.NewGuid(), 1031), Settings(User, 1036));

        Assert.Equal(1036, svc.ResolveUserLanguageCode());
    }

    [Fact]
    public void A_negative_organization_language_reads_as_none()
    {
        MessageService svc = Messages(new Entity("organization", Guid.NewGuid()) { ["languagecode"] = -5 });

        Assert.Equal(0, svc.ResolveOrganizationLanguageCode());
        Assert.Equal(1033, svc.ResolveUserLanguageCode()); // the default language
    }

    [Fact]
    public void A_source_that_answers_without_a_template_does_not_win()
    {
        MessageService svc = Messages();
        svc.AddSource(new NullTemplateSource());
        svc.AddSource(new DictionaryMessageSource().Add(1033, "K", "from the second source"));

        Assert.Equal("from the second source", svc.GetMessageIn(1033, "K"));
    }

    [Fact]
    public void A_template_without_arguments_is_returned_verbatim()
    {
        MessageService svc = Messages();
        svc.AddSource(new DictionaryMessageSource().Add(1033, "K", "Literal {0} placeholder"));

        Assert.Equal("Literal {0} placeholder", svc.GetMessageIn(1033, "K"));
        Assert.Equal("Literal {0} placeholder", svc.GetMessageIn(1033, "K", null!));
    }

    [Fact]
    public void Arguments_are_formatted_in_the_messages_culture_or_invariant_for_an_unknown_one()
    {
        MessageService svc = Messages();
        svc.AddSource(new DictionaryMessageSource().Add(1036, "N", "{0}").Add(999999, "N", "{0}"));

        Assert.Equal(1.5.ToString(CultureInfo.GetCultureInfo(1036)), svc.GetMessageIn(1036, "N", 1.5));
        Assert.Equal("1,5", svc.GetMessageIn(1036, "N", 1.5));
        Assert.Equal("1.5", svc.GetMessageIn(999999, "N", 1.5));
    }

    // ---- OrganizationScopedCacheService ----------------------------------------------------------------------------

    [Fact]
    public void Organization_scoped_keys_live_under_the_org_prefix_of_the_shared_cache()
    {
        PluginCacheService inner = new();
        Guid org = Guid.NewGuid();
        OrganizationScopedCacheService scoped = new(inner, org);

        scoped.GetOrCreate("k", TimeSpan.FromMinutes(1), () => 1);

        Assert.Equal(1, inner.GetOrCreate("org:" + org.ToString("N") + ":k", TimeSpan.FromMinutes(1), () => 2));
    }

    // ---- AssemblyServiceRegistrars ---------------------------------------------------------------------------------

    [Fact]
    public void A_registrar_type_that_is_not_a_registrar_is_named_in_the_error()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => Vela.DI.AssemblyServiceRegistrars.CreateRegistrar(new Vela.DI.VelaServiceRegistrarAttribute(typeof(string))));

        Assert.Equal("[assembly: VelaServiceRegistrar] type 'System.String' must implement IServiceRegistrar.", ex.Message);
    }

    [Fact]
    public void An_abstract_registrar_is_refused_as_not_concrete()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => Vela.DI.AssemblyServiceRegistrars.CreateRegistrar(new Vela.DI.VelaServiceRegistrarAttribute(typeof(AbstractRegistrar))));

        Assert.Equal($"[assembly: VelaServiceRegistrar] type '{typeof(AbstractRegistrar).FullName}' must be a concrete, non-generic class.", ex.Message);
    }

    [Fact]
    public void A_registrar_without_a_parameterless_constructor_says_so()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => Vela.DI.AssemblyServiceRegistrars.CreateRegistrar(new Vela.DI.VelaServiceRegistrarAttribute(typeof(NoDefaultCtorRegistrar))));

        Assert.Equal($"Could not create service registrar '{typeof(NoDefaultCtorRegistrar).FullName}': it needs a public parameterless constructor.", ex.Message);
        Assert.IsType<MissingMethodException>(ex.InnerException);
    }

    public abstract class AbstractRegistrar : Vela.DI.IServiceRegistrar
    {
        public abstract void Register(Vela.DI.SimpleServiceResolver services, ILocalPluginContext context);
    }

    public sealed class NoDefaultCtorRegistrar(int unused) : Vela.DI.IServiceRegistrar
    {
        public int Unused { get; } = unused;

        public void Register(Vela.DI.SimpleServiceResolver services, ILocalPluginContext context) { }
    }

    // ---- DataverseTableMessageSource -----------------------------------------------------------------------------

    private static Entity LabelRow(string key, int lcid, string? value, object? state = null)
    {
        Entity row = new("vela_label", Guid.NewGuid()) { ["vela_key"] = key, ["vela_lcid"] = lcid, ["vela_value"] = value };
        if (state != null)
        {
            row["statecode"] = state;
        }

        return row;
    }

    private static (DataverseTableMessageSource Source, InMemoryOrganizationService Crm) Labels(params Entity[] rows)
    {
        InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(rows);
        ILocalPluginContext ctx = new PluginContextBuilder().WithSystemService(crm).WithOrganizationId(Guid.NewGuid()).Build();
        return (new DataverseTableMessageSource(ctx, new PluginCacheService(), "vela_label", "vela_key", "vela_lcid", "vela_value"), crm);
    }

    [Fact]
    public void A_label_without_a_value_or_marked_inactive_is_ignored()
    {
        (DataverseTableMessageSource source, _) = Labels(
            LabelRow("EMPTY", 1033, null),
            LabelRow("OFF", 1033, "off", state: 1),
            LabelRow("ON", 1033, "on", state: 0));

        Assert.False(source.TryGetTemplate("EMPTY", 1033, out _));
        Assert.False(source.TryGetTemplate("OFF", 1033, out _));
        Assert.True(source.TryGetTemplate("ON", 1033, out string? on));
        Assert.Equal("on", on);
    }

    [Fact]
    public void Labels_are_read_once_per_execution()
    {
        (DataverseTableMessageSource source, InMemoryOrganizationService crm) = Labels(LabelRow("A", 1033, "a"));
        Assert.True(source.TryGetTemplate("A", 1033, out _));

        crm.Create(LabelRow("B", 1033, "b"));

        Assert.False(source.TryGetTemplate("B", 1033, out _));
    }

    [Fact]
    public void A_missing_column_name_is_named_in_the_error()
    {
        ILocalPluginContext ctx = new PluginContextBuilder().Build();

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => new DataverseTableMessageSource(ctx, new PluginCacheService(), " ", "k", "l", "v"));

        Assert.StartsWith("'tableLogicalName' is required.", ex.Message, StringComparison.Ordinal);
    }

    // Claims a hit but hands back no template, like a misbehaving custom source.
    private sealed class NullTemplateSource : IMessageSource
    {
        public bool TryGetTemplate(string key, int languageCode, out string? template)
        {
            template = null;
            return true;
        }
    }
}
