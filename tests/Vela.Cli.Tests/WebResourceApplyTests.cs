using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;
using static Vela.Cli.JsonReader;

namespace Vela.Cli.Tests;

public class WebResourceApplyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Def(string name, int type, string contentPath) =>
        JsonSerializer.SerializeToElement(new { name, webResourceType = type, contentPath }, Web);

    [Fact]
    public void UpsertWebResource_Creates_When_Not_Found()
    {
        string file = TempFile("console.log('a');");
        try
        {
            FakeOrgService svc = new FakeOrgService { OnRetrieveMultiple = _ => new EntityCollection() };

            DataverseRegistration.UpsertWebResource(svc, Def("adc_/js/a.js", 3, file), verbose: false);

            Entity created = Assert.Single(svc.Created);
            Assert.Equal("webresource", created.LogicalName);
            Assert.Equal("adc_/js/a.js", created["name"]);
            Assert.Equal(3, ((OptionSetValue)created["webresourcetype"]).Value);
            Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(file)), created["content"]);
            Assert.Empty(svc.Updated);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void UpsertWebResource_Updates_When_Found()
    {
        string file = TempFile("body{}");
        Guid existingId = Guid.NewGuid();
        try
        {
            FakeOrgService svc = new FakeOrgService
            {
                OnRetrieveMultiple = _ => FakeOrgService.Collection(new Entity("webresource", existingId) { ["name"] = "adc_/styles/theme.css" }),
            };

            Guid returned = DataverseRegistration.UpsertWebResource(svc, Def("adc_/styles/theme.css", 2, file), verbose: false);

            Assert.Equal(existingId, returned);
            Entity updated = Assert.Single(svc.Updated);
            Assert.Equal(existingId, updated.Id);
            Assert.Empty(svc.Created);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void DeleteWebResourceByName_Deletes_When_Found_And_Is_Idempotent()
    {
        Guid id = Guid.NewGuid();
        FakeOrgService found = new FakeOrgService
        {
            OnRetrieveMultiple = _ => FakeOrgService.Collection(new Entity("webresource", id) { ["name"] = "adc_/x.js" }),
        };
        DataverseRegistration.DeleteWebResourceByName(found, "adc_/x.js");
        (string Entity, Guid Id) deleted = Assert.Single(found.Deleted);
        Assert.Equal("webresource", deleted.Entity);
        Assert.Equal(id, deleted.Id);

        FakeOrgService missing = new FakeOrgService { OnRetrieveMultiple = _ => new EntityCollection() };
        DataverseRegistration.DeleteWebResourceByName(missing, "adc_/gone.js");
        Assert.Empty(missing.Deleted);
    }

    [Fact]
    public void PublishWebResources_Issues_PublishXml_With_Ids()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        FakeOrgService svc = new FakeOrgService();

        DataverseRegistration.PublishWebResources(svc, new[] { a, b });

        PublishXmlRequest request = Assert.IsType<PublishXmlRequest>(Assert.Single(svc.Executed));
        Assert.Contains(a.ToString("D"), request.ParameterXml);
        Assert.Contains(b.ToString("D"), request.ParameterXml);
        Assert.Contains("<webresources>", request.ParameterXml);
    }

    [Fact]
    public void PublishWebResources_NoOp_When_Empty()
    {
        FakeOrgService svc = new FakeOrgService();
        DataverseRegistration.PublishWebResources(svc, Array.Empty<Guid>());
        Assert.Empty(svc.Executed);
    }

    [Fact]
    public void ReadWebResourcesInSolution_Reads_And_Hashes_Consistently_With_The_Scanner()
    {
        const string source = "console.log('shared');";
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        Guid solutionId = Guid.NewGuid();
        Guid wrId = Guid.NewGuid();

        FakeOrgService svc = new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "solution" => FakeOrgService.Collection(new Entity("solution", solutionId)),
                "solutioncomponent" => FakeOrgService.Collection(new Entity("solutioncomponent") { ["objectid"] = wrId }),
                _ => new EntityCollection(),
            },
            OnRetrieve = (entity, id, _) => new Entity(entity, id)
            {
                ["name"] = "adc_/js/a.js",
                ["webresourcetype"] = new OptionSetValue(3),
                ["content"] = base64,
            },
        };

        RegistrationItem item = Assert.Single(DataverseRegistration.ReadWebResourcesInSolution(svc, "MySol", Web));
        Assert.Equal("adc_/js/a.js", item.Type);
        Assert.Equal(3, TryGetInt(item.Definition, "webResourceType"));

        // The reader hash must equal the scanner hash for the same bytes, so unchanged content is a no-op diff.
        string scannerHash = ScannerHashFor("adc_/js/a.js", source);
        Assert.Equal(scannerHash, TryGetString(item.Definition, "contentHash"));
    }

    [Fact]
    public void ReadWebResourcesInSolution_Unknown_Solution_Returns_Empty()
    {
        FakeOrgService svc = new FakeOrgService { OnRetrieveMultiple = _ => new EntityCollection() };
        Assert.Empty(DataverseRegistration.ReadWebResourcesInSolution(svc, "Nope", Web));
    }

    private static string ScannerHashFor(string relativePath, string content)
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-wr-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return TryGetString(WebResourceScanner.Scan(root, Web).Single().Definition, "contentHash")!;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-wr-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }
}
