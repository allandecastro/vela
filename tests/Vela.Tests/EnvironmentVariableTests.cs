using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// Environment variable reader: effective value resolution (value record over default), typed conversion,
/// the missing-value policies (throw / fallback / try), and the Secret-type guard. Rows are seeded into the
/// in-memory org, and the service reads them through the context's system service.
/// </summary>
public sealed class EnvironmentVariableTests
{
    private const int TypeString = 100000000;
    private const int TypeNumber = 100000001;
    private const int TypeBoolean = 100000002;
    private const int TypeJson = 100000003;
    private const int TypeSecret = 100000005;

    private static Entity Definition(Guid id, string schemaName, int type, string? defaultValue)
    {
        var e = new Entity("environmentvariabledefinition", id)
        {
            ["schemaname"] = schemaName,
            ["type"] = new OptionSetValue(type),
        };
        if (defaultValue != null)
        {
            e["defaultvalue"] = defaultValue;
        }

        return e;
    }

    private static Entity Value(Guid definitionId, string value) =>
        new Entity("environmentvariablevalue", Guid.NewGuid())
        {
            ["environmentvariabledefinitionid"] = new EntityReference("environmentvariabledefinition", definitionId),
            ["value"] = value,
            ["statecode"] = new OptionSetValue(0),
        };

    private static ILocalPluginContext Context(params Entity[] rows)
    {
        var crm = new InMemoryOrganizationService().Seed(rows);
        return new PluginContextBuilder().WithSystemService(crm).Build();
    }

    // ---- Effective value resolution ---------------------------------------------------------------

    [Fact]
    public void Value_record_overrides_the_default()
    {
        Guid def = Guid.NewGuid();
        ILocalPluginContext ctx = Context(
            Definition(def, "adc_ApiBaseUrl", TypeString, "https://default"),
            Value(def, "https://override"));

        Assert.Equal("https://override", ctx.GetEnvironmentVariable("adc_ApiBaseUrl"));
    }

    [Fact]
    public void Falls_back_to_the_default_when_no_value_record()
    {
        Guid def = Guid.NewGuid();
        ILocalPluginContext ctx = Context(Definition(def, "adc_ApiBaseUrl", TypeString, "https://default"));

        Assert.Equal("https://default", ctx.GetEnvironmentVariable("adc_ApiBaseUrl"));
    }

    [Fact]
    public void An_inactive_value_record_is_ignored_and_the_default_applies()
    {
        Guid def = Guid.NewGuid();
        Entity inactive = Value(def, "https://stale");
        inactive["statecode"] = new OptionSetValue(1); // inactive

        ILocalPluginContext ctx = Context(Definition(def, "adc_ApiBaseUrl", TypeString, "https://default"), inactive);

        Assert.Equal("https://default", ctx.GetEnvironmentVariable("adc_ApiBaseUrl"));
    }

    // ---- Missing-value policies -------------------------------------------------------------------

    [Fact]
    public void Get_throws_when_the_definition_is_missing()
    {
        ILocalPluginContext ctx = Context();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => ctx.GetEnvironmentVariable("adc_Missing"));
        Assert.Contains("not defined", ex.Message);
    }

    [Fact]
    public void Get_throws_when_the_definition_has_no_value_and_no_default()
    {
        Guid def = Guid.NewGuid();
        ILocalPluginContext ctx = Context(Definition(def, "adc_Empty", TypeString, defaultValue: null));

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => ctx.GetEnvironmentVariable("adc_Empty"));
        Assert.Contains("no value", ex.Message);
    }

    [Fact]
    public void Fallback_overload_returns_the_fallback_when_missing()
    {
        ILocalPluginContext ctx = Context();

        Assert.Equal("fb", ctx.GetEnvironmentVariable("adc_Missing", "fb"));
        Assert.Equal(3, ctx.GetEnvironmentVariableAs<int>("adc_Missing", fallback: 3));
    }

    [Fact]
    public void TryGet_returns_false_when_missing_and_true_with_the_value()
    {
        Guid def = Guid.NewGuid();
        ILocalPluginContext ctx = Context(Definition(def, "adc_Flag", TypeString, "on"));

        Assert.False(ctx.TryGetEnvironmentVariable("adc_Missing", out string? missing));
        Assert.Null(missing);

        Assert.True(ctx.TryGetEnvironmentVariable("adc_Flag", out string? present));
        Assert.Equal("on", present);
    }

    // ---- Typed conversion -------------------------------------------------------------------------

    [Fact]
    public void Converts_number_boolean_and_json()
    {
        Guid n = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid j = Guid.NewGuid();
        ILocalPluginContext ctx = Context(
            Definition(n, "adc_MaxRetries", TypeNumber, "5"),
            Definition(b, "adc_FeatureOn", TypeBoolean, "yes"),
            Definition(j, "adc_Config", TypeJson, "{\"Retries\":7,\"Name\":\"x\"}"));

        Assert.Equal(5, ctx.GetEnvironmentVariableAs<int>("adc_MaxRetries"));
        Assert.True(ctx.GetEnvironmentVariableAs<bool>("adc_FeatureOn"));

        Config cfg = ctx.GetEnvironmentVariableAs<Config>("adc_Config");
        Assert.Equal(7, cfg.Retries);
        Assert.Equal("x", cfg.Name);
    }

    [Fact]
    public void TryGetAs_returns_false_when_the_value_cannot_convert()
    {
        Guid def = Guid.NewGuid();
        ILocalPluginContext ctx = Context(Definition(def, "adc_MaxRetries", TypeNumber, "not-a-number"));

        Assert.False(ctx.TryGetEnvironmentVariableAs<int>("adc_MaxRetries", out int value));
        Assert.Equal(0, value);
    }

    // ---- Secret guard -----------------------------------------------------------------------------

    [Fact]
    public void Secret_type_is_rejected()
    {
        Guid def = Guid.NewGuid();
        ILocalPluginContext ctx = Context(Definition(def, "adc_ApiKey", TypeSecret, "kv-ref"));

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => ctx.GetEnvironmentVariable("adc_ApiKey"));
        Assert.Contains("Secret", ex.Message);
    }

    // ---- Caching ----------------------------------------------------------------------------------

    [Fact]
    public void CacheFor_returns_the_resolved_value()
    {
        // Unique name so the process-global cache does not collide with other tests.
        string name = "adc_Cached_" + Guid.NewGuid().ToString("N");
        Guid def = Guid.NewGuid();
        ILocalPluginContext ctx = Context(Definition(def, name, TypeString, "cached"));

        Assert.Equal("cached", ctx.GetEnvironmentVariable(name, TimeSpan.FromMinutes(5)));
        // Second read (served from the per-execution / cross-execution cache) is consistent.
        Assert.Equal("cached", ctx.GetEnvironmentVariable(name, TimeSpan.FromMinutes(5)));
    }

    private sealed class Config
    {
        public int Retries { get; set; }

        public string? Name { get; set; }
    }
}
