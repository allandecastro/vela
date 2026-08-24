using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>
/// Web resource apply/read: the third registration kind (parallel to plugins and Custom APIs). Deploys files from a
/// folder mirror by unique name, publishing changed resources, and reads a solution's current web resources for the
/// diff. Content is base64; a SHA-256 content hash (matching <see cref="WebResourceScanner"/>) drives the diff.
/// </summary>
internal static partial class DataverseRegistration
{
    // componenttype 61 = Web Resource.
    private const int WebResourceComponentType = 61;

    // Upserts one web resource from its scanned definition (name + type + local content path). Returns the row id so
    // the caller can publish it. Create/Update go through the (possibly solution-scoped) service.
    internal static Guid UpsertWebResource(IOrganizationService svc, JsonElement definition, bool verbose)
    {
        string name = TryGetString(definition, "name") ?? throw new InvalidOperationException("Web resource definition is missing 'name'.");
        string contentPath = TryGetString(definition, "contentPath") ?? throw new InvalidOperationException($"Web resource '{name}' has no local content path to deploy.");
        int type = TryGetInt(definition, "webResourceType");
        string content = Convert.ToBase64String(File.ReadAllBytes(contentPath));

        Entity? existing = FindWebResourceByName(svc, name);
        if (existing == null)
        {
            LogOperation("web-resource", "create", name);
            Entity created = new Entity("webresource")
            {
                ["name"] = name,
                ["displayname"] = name,
                ["webresourcetype"] = new OptionSetValue(type),
                ["content"] = content,
            };
            return svc.Create(created);
        }

        LogOperation("web-resource", "update", name);
        Entity update = new Entity("webresource", existing.Id)
        {
            ["content"] = content,
            ["webresourcetype"] = new OptionSetValue(type),
        };
        svc.Update(update);
        return existing.Id;
    }

    internal static void DeleteWebResourceByName(IOrganizationService service, string name)
    {
        Entity? existing = FindWebResourceByName(service, name);
        if (existing == null)
        {
            return; // already gone: deletion is idempotent
        }

        LogOperation("web-resource", "delete", name);
        service.Delete("webresource", existing.Id);
    }

    // Publishes the given web resources so the uploaded content goes live (required after create/update).
    internal static void PublishWebResources(IOrganizationService service, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        StringBuilder xml = new StringBuilder("<importexportxml><webresources>");
        foreach (Guid id in ids)
        {
            xml.Append("<webresource>").Append(id.ToString("D")).Append("</webresource>");
        }

        xml.Append("</webresources></importexportxml>");
        service.Execute(new PublishXmlRequest { ParameterXml = xml.ToString() });
    }

    // Reads the current web resources in a solution (componenttype 61) as diff items, hashing their content the same
    // way the scanner hashes files so equal content produces equal hashes.
    internal static RegistrationItem[] ReadWebResourcesInSolution(IOrganizationService service, string solutionUniqueName, JsonSerializerOptions jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
        {
            return Array.Empty<RegistrationItem>();
        }

        QueryExpression solutionQuery = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid"), TopCount = 1 };
        solutionQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);
        Entity? solution = service.RetrieveMultiple(solutionQuery).Entities.FirstOrDefault();
        if (solution == null)
        {
            return Array.Empty<RegistrationItem>();
        }

        QueryExpression componentQuery = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
        componentQuery.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solution.Id);
        componentQuery.Criteria.AddCondition("componenttype", ConditionOperator.Equal, WebResourceComponentType);
        List<Guid> ids = service.RetrieveAll(componentQuery)
            .Select(e => e.GetAttributeValue<Guid>("objectid"))
            .Distinct()
            .ToList();

        List<RegistrationItem> items = new List<RegistrationItem>();
        foreach (Guid id in ids)
        {
            Entity row = service.Retrieve("webresource", id, new ColumnSet("name", "webresourcetype", "content"));
            string name = row.GetAttributeValue<string>("name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            int type = row.GetAttributeValue<OptionSetValue>("webresourcetype")?.Value ?? 0;
            string hash = HashBase64(row.GetAttributeValue<string>("content"));
            WebResourceDefinition definition = new WebResourceDefinition(name, type, hash, string.Empty);
            items.Add(new RegistrationItem("web-resource", name, JsonSerializer.SerializeToElement(definition, jsonOptions)));
        }

        return items.OrderBy(i => i.Type, StringComparer.Ordinal).ToArray();
    }

    private static Entity? FindWebResourceByName(IOrganizationService service, string name)
    {
        QueryExpression query = new QueryExpression("webresource") { ColumnSet = new ColumnSet("webresourceid", "name"), TopCount = 1 };
        query.Criteria.AddCondition("name", ConditionOperator.Equal, name);
        return service.RetrieveMultiple(query).Entities.FirstOrDefault();
    }

    // SHA-256 of the decoded content, hex lower-case: matches WebResourceScanner.HashFile so live and local
    // definitions of the same bytes compare equal.
    private static string HashBase64(string? base64)
    {
        byte[] bytes = string.IsNullOrEmpty(base64) ? Array.Empty<byte>() : Convert.FromBase64String(base64);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
