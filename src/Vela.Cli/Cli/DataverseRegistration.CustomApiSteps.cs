using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.JsonReader;
using static Vela.Cli.DefinitionParser;
using static Vela.Cli.DiffEngine;

namespace Vela.Cli;

/// <summary>
/// Steps on a Custom API's message. Dataverse creates exactly one message filter per Custom API (verified live): with
/// no table (primary entity "none") for a global API, the bound table for an entity- or collection-bound one. A step is
/// declared from an <c>EntityPluginBase&lt;T&gt;</c>, so it carries the plugin's table whatever the API's binding; the
/// binding decides what is registered.
/// </summary>
internal static partial class DataverseRegistration
{
    /// <summary>
    /// The binding of each Custom API a local step is on, by unique name: the local Custom API definition (a
    /// <c>RegisterCustomApi</c> without <c>BoundTo</c>/<c>BoundToCollection</c> is global), else the live
    /// <c>customapi</c> row, read in one query for the step messages no local definition covers. Only a message with a
    /// publisher prefix (an underscore) can be a Custom API, so a step on a platform message costs no query.
    /// </summary>
    internal static Dictionary<string, (int BindingType, string? BoundEntity)> ResolveCustomApiBindings(IOrganizationService service, RegistrationState local)
    {
        Dictionary<string, (int BindingType, string? BoundEntity)> bindings = LocalCustomApiBindings(local.Items);
        object[] unresolved = local.Items.Where(IsStep)
            .Select(item => TryGetString(item.Definition, "message"))
            .Where(message => !string.IsNullOrWhiteSpace(message) && message!.Contains('_') && !bindings.ContainsKey(message))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray();
        if (unresolved.Length == 0)
        {
            return bindings;
        }

        QueryExpression query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet("uniquename", "bindingtype", "boundentitylogicalname"),
        };
        query.Criteria.AddCondition("uniquename", ConditionOperator.In, unresolved);
        foreach (Entity api in service.RetrieveAll(query))
        {
            if (api.GetAttributeValue<string>("uniquename") is string uniqueName && !bindings.ContainsKey(uniqueName))
            {
                bindings[uniqueName] = (api.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0, api.GetAttributeValue<string>("boundentitylogicalname"));
            }
        }

        return bindings;
    }

    // The binding of each Custom API defined in these items, by unique name (the first definition wins).
    internal static Dictionary<string, (int BindingType, string? BoundEntity)> LocalCustomApiBindings(IEnumerable<RegistrationItem> items)
    {
        Dictionary<string, (int, string?)> bindings = new(StringComparer.OrdinalIgnoreCase);
        foreach (RegistrationItem item in items.Where(item => !IsStep(item) && !IsWebResource(item)))
        {
            if (TryGetString(item.Definition, "uniqueName") is string uniqueName && !bindings.ContainsKey(uniqueName))
            {
                CustomApiApplyDefinition api = ParseCustomApiDefinition(item.Definition);
                bindings[uniqueName] = (api.BindingType, api.BoundEntityLogicalName);
            }
        }

        return bindings;
    }

    // A global Custom API: binding type 0, or a binding with no table.
    internal static bool IsGlobalBinding(int bindingType, string? boundEntity) => bindingType == 0 || string.IsNullOrWhiteSpace(boundEntity);

    /// <summary>
    /// Registers each local step on a global Custom API without its table: the API's only message filter has none, and a
    /// step registered with no filter fires (verified live), seeing <c>PrimaryEntityName</c> "none". The step keeps its
    /// key, so it pairs with the live step, and the live step (no filter, no table) compares equal: no perpetual update.
    /// One info line per step says the plugin's table is ignored. A step on a bound API is left as declared
    /// (<see cref="CustomApiStepTableProblem"/> blocks another table than the bound one).
    /// </summary>
    internal static RegistrationState UnscopeGlobalCustomApiSteps(RegistrationState local, IReadOnlyDictionary<string, (int BindingType, string? BoundEntity)> bindings, JsonSerializerOptions jsonOptions)
    {
        List<string> info = [];
        bool changed = false;
        RegistrationItem[] items = local.Items.Select(item =>
        {
            if (!IsStep(item)
                || TryGetString(item.Definition, "message") is not string message
                || !bindings.TryGetValue(message, out (int BindingType, string? BoundEntity) binding)
                || !IsGlobalBinding(binding.BindingType, binding.BoundEntity)
                || TryGetString(item.Definition, "entityLogicalName") is not { Length: > 0 } table
                || JsonNode.Parse(item.Definition.GetRawText()) is not JsonObject obj)
            {
                return item;
            }

            changed = true;
            string pluginType = TryGetString(item.Definition, "pluginTypeName") ?? item.Type;
            info.Add($"step {pluginType} on {message}: {message} is a global Custom API, registered without a table ({table} ignored)");
            obj["entityLogicalName"] = string.Empty;
            if (string.IsNullOrWhiteSpace(TryGetString(item.Definition, "stepKey")))
            {
                // A keyless step's name carries its table: rebuild it as apply writes it, and the default description with it.
                string oldName = TryGetString(item.Definition, "name") ?? string.Empty;
                string newName = DefinitionNormalizer.BuildKeylessStepName(pluginType, TryGetInt(item.Definition, "stage"), TryGetInt(item.Definition, "mode"), message, string.Empty);
                obj["name"] = newName;
                if (string.Equals(TryGetString(item.Definition, "description"), oldName, StringComparison.Ordinal))
                {
                    obj["description"] = newName;
                }
            }

            return item with { Definition = JsonSerializer.SerializeToElement(obj, jsonOptions) };
        }).ToArray();

        return changed ? local with { Items = items, Info = [.. local.Info ?? [], .. info] } : local;
    }

    /// <summary>
    /// Why a step cannot be registered on a Custom API's message with its table, or null when it can. A bound API's only
    /// message filter is on its bound table: a step with another table has no filter to register on (one created with
    /// another message's filter is accepted but never fires). A step with no table registers with no filter. A step on
    /// a global API never reaches this with a table: <see cref="UnscopeGlobalCustomApiSteps"/> drops it first.
    /// </summary>
    internal static string? CustomApiStepTableProblem(StepApplyDefinition definition, int bindingType, string? boundEntity)
    {
        string table = definition.EntityLogicalName;
        if (string.IsNullOrWhiteSpace(table) || IsGlobalBinding(bindingType, boundEntity)
            || string.Equals(table, boundEntity, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"Cannot register step '{BuildStepName(definition)}': Custom API '{definition.Message}' is bound to '{boundEntity}', its only " +
            $"message filter, so a step on it cannot be scoped to '{table}'.";
    }
}
