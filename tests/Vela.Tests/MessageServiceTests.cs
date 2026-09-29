using System;
using Vela.Caching;
using Vela.Context;
using Vela.Localization;
using Vela.Plugin;
using Vela.Testing;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// Message service (i18n): language resolution from <c>usersettings.uilanguageid</c> with a default fallback,
/// source ordering, parameterized formatting, the dictionary and Dataverse-table sources, and the context
/// helpers (<c>GetMessage</c> / <c>TryGetMessage</c> / <c>BusinessError</c>). Rows are seeded into the in-memory
/// org and read through the context's system service, mirroring <see cref="EnvironmentVariableTests"/>.
/// </summary>
public sealed class MessageServiceTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();

    private static Entity UserSettings(int uiLanguageId) =>
        new Entity("usersettings", Guid.NewGuid())
        {
            ["systemuserid"] = User,
            ["uilanguageid"] = uiLanguageId,
        };

    private static Entity Label(string key, int lcid, string value) =>
        new Entity("vela_label", Guid.NewGuid())
        {
            ["vela_key"] = key,
            ["vela_lcid"] = lcid,
            ["vela_value"] = value,
        };

    private static ILocalPluginContext Context(params Entity[] rows)
    {
        var crm = new InMemoryOrganizationService().Seed(rows);
        return new PluginContextBuilder()
            .WithSystemService(crm)
            .WithInitiatingUserId(User)
            .WithOrganizationId(Org)
            .Build();
    }

    private static MessageService Service(ILocalPluginContext ctx) => new MessageService(ctx, new PluginCacheService());

    // ---- Language resolution ----------------------------------------------------------------------

    [Fact]
    public void Resolves_the_message_in_the_users_language()
    {
        ILocalPluginContext ctx = Context(UserSettings(1036));
        MessageService svc = Service(ctx);
        svc.AddSource(new DictionaryMessageSource()
            .Add(1033, "GREETING", "Hello")
            .Add(1036, "GREETING", "Bonjour"));

        Assert.Equal("Bonjour", svc.GetMessage("GREETING"));
    }

    [Fact]
    public void Falls_back_to_the_default_language_when_the_user_language_has_no_template()
    {
        ILocalPluginContext ctx = Context(UserSettings(1036));
        MessageService svc = Service(ctx);
        svc.AddSource(new DictionaryMessageSource().Add(1033, "GREETING", "Hello")); // no 1036 entry

        Assert.Equal("Hello", svc.GetMessage("GREETING"));
    }

    [Fact]
    public void Falls_back_to_the_default_language_code_when_no_user_setting_exists()
    {
        ILocalPluginContext ctx = Context(); // no usersettings row seeded
        MessageService svc = Service(ctx);
        svc.AddSource(new DictionaryMessageSource().Add(1033, "GREETING", "Hello"));

        Assert.Equal(1033, svc.ResolveUserLanguageCode());
        Assert.Equal("Hello", svc.GetMessage("GREETING"));
    }

    [Fact]
    public void Honors_a_custom_default_language_code()
    {
        ILocalPluginContext ctx = Context(); // no usersettings
        MessageService svc = Service(ctx);
        svc.DefaultLanguageCode = 1036;
        svc.AddSource(new DictionaryMessageSource().Add(1036, "GREETING", "Bonjour"));

        Assert.Equal("Bonjour", svc.GetMessage("GREETING"));
    }

    // ---- Formatting -------------------------------------------------------------------------------

    [Fact]
    public void Formats_the_template_with_arguments()
    {
        ILocalPluginContext ctx = Context(UserSettings(1033));
        MessageService svc = Service(ctx);
        svc.AddSource(new DictionaryMessageSource().Add(1033, "ERR_REQUIRED", "The field {0} is required."));

        Assert.Equal("The field name is required.", svc.GetMessage("ERR_REQUIRED", "name"));
    }

    // ---- Source ordering --------------------------------------------------------------------------

    [Fact]
    public void First_source_with_a_template_wins()
    {
        ILocalPluginContext ctx = Context(UserSettings(1033));
        MessageService svc = Service(ctx);
        svc.AddSource(new DictionaryMessageSource().Add(1033, "KEY", "first"));
        svc.AddSource(new DictionaryMessageSource().Add(1033, "KEY", "second"));

        Assert.Equal("first", svc.GetMessage("KEY"));
    }

    // ---- Missing-key policy -----------------------------------------------------------------------

    [Fact]
    public void GetMessage_throws_for_an_unknown_key()
    {
        ILocalPluginContext ctx = Context(UserSettings(1033));
        MessageService svc = Service(ctx);

        Assert.Throws<InvalidPluginExecutionException>(() => svc.GetMessage("MISSING"));
    }

    [Fact]
    public void TryGetMessage_returns_false_for_an_unknown_key()
    {
        ILocalPluginContext ctx = Context(UserSettings(1033));
        MessageService svc = Service(ctx);

        Assert.False(svc.TryGetMessage("MISSING", out string? message));
        Assert.Null(message);
    }

    [Fact]
    public void GetMessageIn_resolves_an_explicit_language()
    {
        ILocalPluginContext ctx = Context(UserSettings(1033));
        MessageService svc = Service(ctx);
        svc.AddSource(new DictionaryMessageSource().Add(1036, "GREETING", "Bonjour"));

        Assert.Equal("Bonjour", svc.GetMessageIn(1036, "GREETING"));
    }

    // ---- Dataverse table source -------------------------------------------------------------------

    [Fact]
    public void Reads_templates_from_a_dataverse_labels_table()
    {
        ILocalPluginContext ctx = Context(
            UserSettings(1036),
            Label("GREETING", 1033, "Hello"),
            Label("GREETING", 1036, "Bonjour {0}"));
        MessageService svc = Service(ctx);
        svc.AddSource(new DataverseTableMessageSource(ctx, new PluginCacheService(), "vela_label", "vela_key", "vela_lcid", "vela_value"));

        Assert.Equal("Bonjour Alice", svc.GetMessage("GREETING", "Alice"));
    }

    // ---- Context helpers --------------------------------------------------------------------------

    [Fact]
    public void Context_GetMessage_uses_the_registered_service()
    {
        ILocalPluginContext ctx = Context(UserSettings(1036));
        ctx.GetRequiredService<IMessageService>()
            .AddSource(new DictionaryMessageSource().Add(1036, "GREETING", "Bonjour"));

        Assert.Equal("Bonjour", ctx.GetMessage("GREETING"));
    }

    [Fact]
    public void Context_BusinessError_carries_the_localized_message()
    {
        ILocalPluginContext ctx = Context(UserSettings(1036));
        ctx.GetRequiredService<IMessageService>()
            .AddSource(new DictionaryMessageSource().Add(1036, "ERR_X", "Le champ {0} est requis."));

        PluginBusinessException error = ctx.BusinessError("ERR_X", "nom");

        Assert.Equal("Le champ nom est requis.", error.Message);
    }

    // ---- Dictionary source guards -----------------------------------------------------------------

    [Fact]
    public void DictionaryMessageSource_rejects_an_empty_key()
    {
        Assert.Throws<ArgumentException>(() => new DictionaryMessageSource().Add(1033, "", "x"));
    }

    [Fact]
    public void DictionaryMessageSource_TryGetTemplate_finds_and_misses()
    {
        var source = new DictionaryMessageSource().Add(1033, "KEY", "value");

        Assert.True(source.TryGetTemplate("KEY", 1033, out string? found));
        Assert.Equal("value", found);
        Assert.False(source.TryGetTemplate("KEY", 1036, out _));
        Assert.False(source.TryGetTemplate("OTHER", 1033, out _));
        Assert.False(source.TryGetTemplate("", 1033, out _));
    }

    [Fact]
    public void DictionaryMessageSource_rejects_a_null_template()
    {
        Assert.Throws<ArgumentNullException>(() => new DictionaryMessageSource().Add(1033, "KEY", null!));
    }

    [Fact]
    public void DictionaryMessageSource_adds_a_whole_map_for_a_language()
    {
        var source = new DictionaryMessageSource().Add(1036, new System.Collections.Generic.Dictionary<string, string>
        {
            ["A"] = "Un",
            ["B"] = "Deux",
        });

        Assert.True(source.TryGetTemplate("A", 1036, out string? a));
        Assert.Equal("Un", a);
        Assert.True(source.TryGetTemplate("B", 1036, out string? b));
        Assert.Equal("Deux", b);
    }

    [Fact]
    public void AddSource_rejects_null()
    {
        ILocalPluginContext ctx = Context();
        Assert.Throws<ArgumentNullException>(() => Service(ctx).AddSource(null!));
    }

    // ---- Language column shapes on the labels table -----------------------------------------------

    [Theory]
    [InlineData("optionset")]
    [InlineData("long")]
    [InlineData("string")]
    public void Labels_table_reads_the_language_column_as_number_choice_long_or_text(string shape)
    {
        object lcid = shape switch
        {
            "optionset" => new OptionSetValue(1036),
            "long" => 1036L,
            "string" => "1036",
            _ => 1036,
        };
        var label = new Entity("vela_label", Guid.NewGuid())
        {
            ["vela_key"] = "GREETING",
            ["vela_lcid"] = lcid,
            ["vela_value"] = "Bonjour",
        };
        ILocalPluginContext ctx = Context(UserSettings(1036), label);
        MessageService svc = Service(ctx);
        svc.AddSource(new DataverseTableMessageSource(ctx, new PluginCacheService(), "vela_label", "vela_key", "vela_lcid", "vela_value"));

        Assert.Equal("Bonjour", svc.GetMessage("GREETING"));
    }

    [Fact]
    public void Labels_table_skips_rows_missing_a_key_or_value()
    {
        var noKey = new Entity("vela_label", Guid.NewGuid()) { ["vela_lcid"] = 1033, ["vela_value"] = "orphan" };
        var noValue = new Entity("vela_label", Guid.NewGuid()) { ["vela_key"] = "K", ["vela_lcid"] = 1033 };
        var good = new Entity("vela_label", Guid.NewGuid()) { ["vela_key"] = "K", ["vela_lcid"] = 1033, ["vela_value"] = "kept" };
        ILocalPluginContext ctx = Context(UserSettings(1033), noKey, noValue, good);
        MessageService svc = Service(ctx);
        svc.AddSource(new DataverseTableMessageSource(ctx, new PluginCacheService(), "vela_label", "vela_key", "vela_lcid", "vela_value"));

        Assert.Equal("kept", svc.GetMessage("K"));
    }

    [Fact]
    public void DataverseTableMessageSource_rejects_missing_configuration()
    {
        ILocalPluginContext ctx = Context();
        var cache = new PluginCacheService();
        Assert.Throws<ArgumentException>(() => new DataverseTableMessageSource(ctx, cache, "", "k", "l", "v"));
        Assert.Throws<ArgumentException>(() => new DataverseTableMessageSource(ctx, cache, "t", "", "l", "v"));
        Assert.Throws<ArgumentException>(() => new DataverseTableMessageSource(ctx, cache, "t", "k", "", "v"));
        Assert.Throws<ArgumentException>(() => new DataverseTableMessageSource(ctx, cache, "t", "k", "l", ""));
        Assert.Throws<ArgumentNullException>(() => new DataverseTableMessageSource(null!, cache, "t", "k", "l", "v"));
        Assert.Throws<ArgumentNullException>(() => new DataverseTableMessageSource(ctx, null!, "t", "k", "l", "v"));
    }

    [Fact]
    public void DataverseTableMessageSource_TryGetTemplate_rejects_an_empty_key()
    {
        ILocalPluginContext ctx = Context();
        var source = new DataverseTableMessageSource(ctx, new PluginCacheService(), "vela_label", "vela_key", "vela_lcid", "vela_value");
        Assert.False(source.TryGetTemplate("", 1033, out string? template));
        Assert.Null(template);
    }

    [Fact]
    public void DictionaryMessageSource_Add_map_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => new DictionaryMessageSource().Add(1033, null!));
    }

    [Fact]
    public void Labels_table_treats_a_row_without_a_language_as_language_zero()
    {
        var row = new Entity("vela_label", Guid.NewGuid()) { ["vela_key"] = "K", ["vela_value"] = "neutral" }; // no vela_lcid
        ILocalPluginContext ctx = Context(row);
        MessageService svc = Service(ctx);
        svc.AddSource(new DataverseTableMessageSource(ctx, new PluginCacheService(), "vela_label", "vela_key", "vela_lcid", "vela_value"));

        Assert.Equal("neutral", svc.GetMessageIn(0, "K"));
    }

    [Fact]
    public void A_zero_ui_language_setting_falls_back_to_the_default()
    {
        ILocalPluginContext ctx = Context(UserSettings(0));
        MessageService svc = Service(ctx);
        svc.AddSource(new DictionaryMessageSource().Add(1033, "K", "default"));

        Assert.Equal(1033, svc.ResolveUserLanguageCode());
        Assert.Equal("default", svc.GetMessage("K"));
    }

    // ---- Caching ----------------------------------------------------------------------------------

    [Fact]
    public void Labels_table_can_cache_across_executions()
    {
        var label = new Entity("vela_label", Guid.NewGuid()) { ["vela_key"] = "K", ["vela_lcid"] = 1033, ["vela_value"] = "cached" };
        ILocalPluginContext ctx = Context(UserSettings(1033), label);
        MessageService svc = Service(ctx);
        svc.AddSource(new DataverseTableMessageSource(ctx, new PluginCacheService(), "vela_label", "vela_key", "vela_lcid", "vela_value", cacheFor: TimeSpan.FromMinutes(5)));

        Assert.Equal("cached", svc.GetMessage("K"));
        Assert.Equal("cached", svc.GetMessage("K")); // second read hits the cache
    }

    [Fact]
    public void ResolveUserLanguageCode_can_cache_across_executions()
    {
        ILocalPluginContext ctx = Context(UserSettings(1036));
        MessageService svc = Service(ctx);

        Assert.Equal(1036, svc.ResolveUserLanguageCode(TimeSpan.FromMinutes(5)));
        Assert.Equal(1036, svc.ResolveUserLanguageCode(TimeSpan.FromMinutes(5)));
    }

    // ---- Formatting culture edge cases ------------------------------------------------------------

    [Fact]
    public void Formats_with_the_invariant_culture_for_language_zero()
    {
        ILocalPluginContext ctx = Context();
        MessageService svc = Service(ctx);
        svc.AddSource(new DictionaryMessageSource().Add(0, "N", "Value {0}"));

        Assert.Equal("Value 5", svc.GetMessageIn(0, "N", 5));
    }

    [Fact]
    public void Falls_back_to_invariant_culture_for_an_unknown_lcid()
    {
        ILocalPluginContext ctx = Context();
        MessageService svc = Service(ctx);
        svc.AddSource(new DictionaryMessageSource().Add(1033, "N", "Value {0}"));

        // 999999 has no template (falls back to 1033) and is not a real culture (formats invariant, no throw).
        Assert.Equal("Value 5", svc.GetMessageIn(999999, "N", 5));
    }

    // ---- Empty-key guards -------------------------------------------------------------------------

    [Fact]
    public void GetMessageIn_rejects_an_empty_key()
    {
        ILocalPluginContext ctx = Context(UserSettings(1033));
        Assert.Throws<ArgumentException>(() => Service(ctx).GetMessageIn(1033, ""));
    }

    [Fact]
    public void TryGetMessage_returns_false_for_an_empty_key()
    {
        ILocalPluginContext ctx = Context(UserSettings(1033));
        Assert.False(Service(ctx).TryGetMessage("", out string? message));
        Assert.Null(message);
    }
}
