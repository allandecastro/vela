using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Opt-in LIVE integration smoke: reads real published forms + table metadata from a Dataverse org and generates
/// the vela-client FormModel .ts, exercising FormReader -> FormXmlParser -> FormModelTsGenerator end to end. Off by
/// default; enable with VELA_LIVE_URL and VELA_LIVE_TOKEN (a bearer token whose audience is the org URL). Read-only:
/// it never writes to Dataverse.
/// </summary>
public class LiveDataverseGenerationTests
{
    [Fact]
    public void Generates_Form_Models_From_A_Real_Org()
    {
        string? url = Environment.GetEnvironmentVariable("VELA_LIVE_URL");
        string? token = Environment.GetEnvironmentVariable("VELA_LIVE_TOKEN");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            return; // opt-in only
        }

        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        string[] tables = (Environment.GetEnvironmentVariable("VELA_LIVE_TABLES") ?? "account,contact")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        using ServiceClient service = new ServiceClient(new Uri(url), _ => Task.FromResult(token!), useUniqueInstance: true);
        Assert.True(service.IsReady, service.LastError);

        // 1) Read the published Main/Quick Create forms for the scoped tables (the real FormReader path).
        IReadOnlyList<SystemForm> forms = FormReader.ReadForms(service, tables);
        Assert.NotEmpty(forms);

        // 2) One metadata read per table, then build a FormSource per form and generate the .ts.
        // Real modern formxml carries attributes newer than the embedded 9.0 XSD, so strict validation is only a
        // soft signal here (the parser is deliberately tolerant); count it, don't fail on it.
        int schemaValid = forms.Count(f => FormXmlSchema.IsValid(f.FormXml));

        Dictionary<string, EntityMetadata> metadata = new(StringComparer.OrdinalIgnoreCase);
        List<FormSource> sources = new();
        foreach (SystemForm form in forms)
        {
            if (!metadata.TryGetValue(form.ObjectTypeCode, out EntityMetadata? entity))
            {
                var resp = (RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
                {
                    LogicalName = form.ObjectTypeCode,
                    EntityFilters = EntityFilters.Attributes | EntityFilters.Entity,
                });
                entity = resp.EntityMetadata;
                metadata[form.ObjectTypeCode] = entity;
            }

            sources.Add(new FormSource(form.FormId.ToString("D"), form.FormType, form.Name, FormXmlParser.Parse(form.FormXml), entity));
        }

        IReadOnlyList<GeneratedModel> models = FormModelTsGenerator.Generate(sources);
        Assert.NotEmpty(models);
        Assert.Contains(models, m => m.FileName == "option-sets.ts" || m.FileName.EndsWith(".form.ts", StringComparison.Ordinal));

        // 3) Write them out so the run is inspectable, and surface a short summary on the test output.
        string outDir = Path.Combine(Path.GetTempPath(), "vela-live-gen");
        Directory.CreateDirectory(outDir);
        foreach (GeneratedModel model in models)
        {
            File.WriteAllText(Path.Combine(outDir, model.FileName), model.Code);
        }

        Console.WriteLine($"LIVE: {forms.Count} form(s) ({schemaValid} strictly XSD-valid), {metadata.Count} table(s) -> {models.Count} file(s) in {outDir}");
        foreach (GeneratedModel model in models)
        {
            Console.WriteLine($"  {model.FileName} ({model.Code.Length} bytes)");
        }
    }
}
