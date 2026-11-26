using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Vela.Cli;

/// <summary>One JSON document a command writes: its name in <c>vela help --json</c> and, for plan/apply, the
/// <c>mode</c> values the command can write.</summary>
internal sealed record CliOutput(string Document, string[]? Modes = null);

/// <summary>
/// The versioned JSON documents of the CLI and their JSON Schemas, for <c>vela help --json</c>. Each schema is exported
/// from the C# type the command serializes, with the options it serializes with, so it cannot drift from the output.
/// </summary>
internal static class OutputDocuments
{
    public const string Plan = "plan";
    public const string Inspect = "inspect";
    public const string Solution = "solution";

    /// <summary>Each document, the type that is serialized, and whether the command uses the CLI's JSON options
    /// (plan/apply/inspect) or its own (<c>solution show</c>).</summary>
    private static readonly (string Name, Type Type, bool CliOptions)[] Documents =
    [
        (Plan, typeof(PlanPreview), true),
        (Inspect, typeof(InspectDocument), true),
        (Solution, typeof(SolutionDocument), false),
    ];

    public static IEnumerable<string> Names => Documents.Select(d => d.Name);

    /// <summary>The JSON Schema of every document, keyed by name.</summary>
    public static Dictionary<string, JsonNode> Schemas(JsonSerializerOptions cliOptions) =>
        Documents.ToDictionary(d => d.Name, d => Schema(d.Type, d.CliOptions ? cliOptions : SolutionCommand.DocumentJson), StringComparer.Ordinal);

    private static JsonNode Schema(Type type, JsonSerializerOptions serializerOptions)
    {
        JsonSerializerOptions options = new(serializerOptions) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        JsonNode schema = options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        DropNullableFromRequired(schema);

        // schemaVersion is an init property, so the exporter does not list it, yet every document starts with it.
        JsonObject root = schema.AsObject();
        JsonArray required = root["required"] as JsonArray ?? [];
        required.Insert(0, "schemaVersion");
        root["required"] = required;

        JsonObject document = new() { ["$schema"] = "https://json-schema.org/draft/2020-12/schema" };
        foreach (KeyValuePair<string, JsonNode?> property in root.ToArray())
        {
            root.Remove(property.Key);
            document[property.Key] = property.Value;
        }

        return document;
    }

    // The exporter lists every constructor parameter as required, but the CLI omits null values (WhenWritingNull):
    // a property whose schema allows null may be absent, so it is not required.
    private static void DropNullableFromRequired(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                DropNullableFromRequired(item);
            }

            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        if (obj["properties"] is JsonObject properties && obj["required"] is JsonArray required)
        {
            foreach (JsonNode? name in required.ToArray())
            {
                if (properties[name!.GetValue<string>()] is JsonObject property && AllowsNull(property))
                {
                    required.Remove(name);
                }
            }

            if (required.Count == 0)
            {
                obj.Remove("required");
            }
        }

        foreach (KeyValuePair<string, JsonNode?> child in obj.ToArray())
        {
            DropNullableFromRequired(child.Value);
        }
    }

    private static bool AllowsNull(JsonObject property) => property["type"] switch
    {
        JsonArray types => types.Any(t => t?.GetValue<string>() == "null"),
        JsonValue single => single.GetValue<string>() == "null",
        _ => false,
    };
}
