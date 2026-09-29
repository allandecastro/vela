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
    }
}
