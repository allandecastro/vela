using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// #691: <c>vela help --json</c> lists, per command, the exit codes it can return and the JSON documents it writes,
/// with a JSON Schema per document. These pin the schemas to the documents the commands really serialize.
/// </summary>
public sealed class OutputDocumentsTests
{
    // The options Program.cs serializes plan/apply/inspect with.
    private static readonly JsonSerializerOptions CliOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonElement Help() => JsonDocument.Parse(CliCommands.ToJson(CliOptions)).RootElement;

    private static JsonElement Command(JsonElement help, string name) =>
        help.GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static int[] Codes(JsonElement command) =>
        command.GetProperty("exitCodes").EnumerateArray().Select(e => e.GetProperty("code").GetInt32()).ToArray();

    private static JsonElement Schema(string document) =>
        Help().GetProperty("documents").GetProperty(document).GetProperty("schema");

    [Fact]
    public void Each_command_lists_only_the_exit_codes_it_can_return()
    {
        JsonElement help = Help();

        Assert.Equal([0, 2, 3, 4, 6, 8], Codes(Command(help, "plan")));
        Assert.Equal([0, 2, 3, 4, 5, 6, 7], Codes(Command(help, "apply")));
        Assert.Equal([0, 2, 3, 4, 6], Codes(Command(help, "inspect")));
        Assert.Equal([0, 2, 3, 4, 5, 6], Codes(Command(help, "models")));
        Assert.Equal([0, 2, 4, 6], Codes(Command(help, "solution")));
        Assert.Equal([0, 2], Codes(Command(help, "logout")));
        Assert.Equal([0], Codes(Command(help, "version")));
        JsonElement partial = Command(help, "apply").GetProperty("exitCodes").EnumerateArray().Single(e => e.GetProperty("code").GetInt32() == 7);
        Assert.Contains("apply-failed", partial.GetProperty("description").GetString());
    }

    [Fact]
    public void Every_command_has_exit_codes_that_the_global_table_describes()
    {
        JsonElement help = Help();
        HashSet<string> global = help.GetProperty("exitCodes").EnumerateObject().Select(p => p.Name).ToHashSet();

        foreach (JsonElement command in help.GetProperty("commands").EnumerateArray())
        {
            int[] codes = Codes(command);
            Assert.Contains(0, codes);
            Assert.Equal(codes.Distinct().Count(), codes.Length);
            Assert.All(codes, c => Assert.Contains(c.ToString(System.Globalization.CultureInfo.InvariantCulture), global));
            Assert.All(command.GetProperty("exitCodes").EnumerateArray(), e => Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("description").GetString())));
        }
    }

    [Fact]
    public void Each_command_names_the_documents_it_writes_and_every_document_has_a_schema()
    {
        JsonElement help = Help();
        JsonElement documents = help.GetProperty("documents");

        Assert.Equal(["inspect", "plan", "solution"], documents.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        JsonElement apply = Assert.Single(Command(help, "apply").GetProperty("outputs").EnumerateArray());
        Assert.Equal("plan", apply.GetProperty("document").GetString());
        Assert.Equal(["apply-execute", "apply-not-converged", "apply-failed"], apply.GetProperty("modes").EnumerateArray().Select(m => m.GetString()));
        Assert.Empty(Command(help, "logout").GetProperty("outputs").EnumerateArray());
        foreach (JsonProperty document in documents.EnumerateObject())
        {
            Assert.Equal(MachineOutput.SchemaVersion, document.Value.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.Value.GetProperty("schema").GetProperty("$schema").GetString());
            Assert.Equal("schemaVersion", document.Value.GetProperty("schema").GetProperty("required")[0].GetString());
        }
    }

    [Fact]
    public void An_apply_failed_document_with_every_optional_field_matches_the_plan_schema()
    {
        JsonElement definition = JsonSerializer.SerializeToElement(new { message = "Create" });
        var item = new RegistrationItem("step", "Zava.Plugins.AccountPlugin", definition);
        var diff = new RegistrationDiff(["Zava.Plugins"], [], [item], [], [new RegistrationChange("step", "Zava.Plugins.Other", definition, definition)], [item], []);
        var preview = new PlanPreview(
            "apply-failed", null, true, diff.Summary, [new ApplyOperation("create", "step", item.Type, "Exists locally but not in Dataverse.")], diff,
            ["a warning"], ["a note"],
            Executed: new ExecutedCounts(1, 0, 0, true, [new ExecutedByKind("step", 1, 0, 0)], AddedToSolution: 1),
            Unmanaged: ["Zava.Plugins.Legacy"], Info: ["first deployment"],
            ManagedIdentity: new ManagedIdentityPlan(Guid.NewGuid(), [new ManagedIdentityPackageLink("zava_Zava.Plugins", ManagedIdentityPackageLink.Change, Guid.NewGuid())]),
            Failure: new ApplyFailure("boom", ["a"], ["b"], ["c"]));

        JsonElement json = JsonSerializer.SerializeToElement(preview, CliOptions);

        Assert.False(json.TryGetProperty("environment", out _)); // null, so omitted: the schema does not require it
        AssertMatches(Schema("plan"), json);
    }

    [Fact]
    public void A_plan_document_without_the_optional_fields_matches_the_plan_schema()
    {
        var diff = new RegistrationDiff([], [], [], [], [], [], []);
        var preview = new PlanPreview("plan", "https://zava.crm.dynamics.com", false, diff.Summary, [], diff, [], []);

        AssertMatches(Schema("plan"), JsonSerializer.SerializeToElement(preview, CliOptions));
    }

    [Fact]
    public void The_inspect_document_keeps_its_json_and_matches_its_schema()
    {
        JsonElement definition = JsonSerializer.SerializeToElement(new { message = "Create" });
        var document = new InspectDocument(
            "inspect", "dev",
            new InspectSide(["Zava.Plugins"], [new InspectItem("step", "Zava.Plugins.AccountPlugin", definition)], []),
            new InspectSide([], [], []),
            [], ["a warning"], []);

        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions(CliOptions) { WriteIndented = false });

        // The shape the anonymous object wrote before it became a record.
        Assert.Equal(
            "{\"schemaVersion\":1,\"mode\":\"inspect\",\"environment\":\"dev\","
            + "\"local\":{\"assemblies\":[\"Zava.Plugins\"],\"steps\":[{\"kind\":\"step\",\"type\":\"Zava.Plugins.AccountPlugin\",\"definition\":{\"message\":\"Create\"}}],\"customApis\":[]},"
            + "\"current\":{\"assemblies\":[],\"steps\":[],\"customApis\":[]},\"unmanaged\":[],\"diagnostics\":[\"a warning\"],\"info\":[]}",
            json);
        AssertMatches(Schema("inspect"), JsonDocument.Parse(json).RootElement);
    }

    [Fact]
    public void The_solution_document_starts_with_its_schema_version_and_matches_its_schema()
    {
        string json = JsonSerializer.Serialize(new SolutionDocument("ZavaSales", "zava"), SolutionCommand.DocumentJson);

        Assert.Equal("{\"schemaVersion\":1,\"solution\":\"ZavaSales\",\"publisherPrefix\":\"zava\"}", json);
        AssertMatches(Schema("solution"), JsonDocument.Parse(json).RootElement);
    }

    // A small subset of JSON Schema, enough for the exported schemas: $ref, type, properties, required, items.
    // Every property of the document must be declared, so a field added to a document without its schema fails here.
    private static void AssertMatches(JsonElement root, JsonElement value) => Match(root, root, value, "$");

    private static void Match(JsonElement root, JsonElement schema, JsonElement value, string path)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            JsonElement target = root;
            foreach (string segment in reference.GetString()!.TrimStart('#', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                target = target.GetProperty(segment);
            }

            Match(root, target, value, path);
            return;
        }

        string[] types = schema.TryGetProperty("type", out JsonElement type)
            ? type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToArray() : [type.GetString()!]
            : [];
        string actual = value.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "integer",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null",
        };
        Assert.True(types.Length == 0 || types.Contains(actual), $"{path}: {actual} is not one of [{string.Join(", ", types)}]");

        if (value.ValueKind == JsonValueKind.Object)
        {
            JsonElement properties = schema.GetProperty("properties");
            foreach (JsonProperty property in value.EnumerateObject())
            {
                Assert.True(properties.TryGetProperty(property.Name, out JsonElement propertySchema), $"{path}.{property.Name} is not in the schema");
                Match(root, propertySchema, property.Value, $"{path}.{property.Name}");
            }

            if (schema.TryGetProperty("required", out JsonElement required))
            {
                foreach (JsonElement name in required.EnumerateArray())
                {
                    Assert.True(value.TryGetProperty(name.GetString()!, out _), $"{path}.{name.GetString()} is required but absent");
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
        {
            int index = 0;
            foreach (JsonElement element in value.EnumerateArray())
            {
                Match(root, items, element, $"{path}[{index++}]");
            }
        }
    }
}
