using System;
using System.Text.Json;
using Vela.Cli;

namespace Vela.Cli.Tests.TestSupport;

/// <summary>
/// Helpers for constructing registration state in tests.
/// </summary>
internal static class RegistrationFactory
{
    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public static RegistrationItem Step(object definition, string type = "Zava.Plugins.AccountPlugin")
        => new RegistrationItem("step", type, Json(definition));

    public static RegistrationItem CustomApi(object definition, string type = "Zava.Plugins.MyApi")
        => new RegistrationItem("custom-api", type, Json(definition));

    public static RegistrationState State(params RegistrationItem[] items)
        => new RegistrationState(
            SourceAssemblies: [],
            SourceAssemblyPayloads: [],
            PackagePayloads: [],
            GeneratedAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Items: items,
            Diagnostics: []);
}
