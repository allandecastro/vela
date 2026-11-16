using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli.Tests.TestSupport;

/// <summary>
/// An <see cref="IOrganizationService"/> that keeps state: what apply writes is what the next read returns. It covers
/// the subset of Dataverse the registration commands use: create/update/delete (direct, as requests and inside
/// ExecuteMultiple), SetState, Retrieve, and QueryExpression with Equal/NotEqual/In/Null/NotNull conditions, nested
/// filters, inner/left-outer links (columns come back as AliasedValue under the link alias) and TopCount. Text
/// comparisons are case-insensitive, like Dataverse.
/// <para>
/// Importing a plugin package simulates the platform: it materializes the package's plugin assembly and plugin types
/// (the CLI waits for those after the upload). The SDK message catalogue is seeded with
/// <see cref="SeedMessage"/>; creating a Custom API adds its message, as the platform does.
/// </para>
/// </summary>
internal sealed class StatefulOrgService : IOrganizationService
{
    private readonly Dictionary<string, Dictionary<Guid, Entity>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string AssemblyName, string[] TypeNames)> _packageContents = new(StringComparer.OrdinalIgnoreCase);
    private readonly PackageFileStore _packageFiles = new();
    private DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Every write in order, as "create:entity", "update:entity", "delete:entity" or "setstate:entity".</summary>
    public List<string> Writes { get; } = new();

    /// <summary>Declares what importing the named plugin package materializes (the platform reads it from the .nupkg).</summary>
    public void DefinePackage(string packageName, string assemblyName, params string[] pluginTypeNames)
        => _packageContents[packageName] = (assemblyName, pluginTypeNames);

    /// <summary>Seeds an SDK message and, for each entity, its sdkmessagefilter.</summary>
    public void SeedMessage(string messageName, params string[] entities)
    {
        Guid messageId = Insert(new Entity("sdkmessage") { ["name"] = messageName });
        foreach (string entity in entities)
        {
            Insert(new Entity("sdkmessagefilter")
            {
                ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
                ["primaryobjecttypecode"] = entity,
            });
        }
    }

    public IReadOnlyCollection<Entity> Rows(string entityName) =>
        _tables.TryGetValue(entityName, out Dictionary<Guid, Entity>? rows) ? rows.Values.Select(Clone).ToArray() : [];

    public Guid Create(Entity entity)
    {
        Writes.Add("create:" + entity.LogicalName);
        Guid id = Insert(entity);
        if (string.Equals(entity.LogicalName, "pluginpackage", StringComparison.OrdinalIgnoreCase))
        {
            ImportPackage(Table("pluginpackage")[id]);
        }
        else if (string.Equals(entity.LogicalName, "customapi", StringComparison.OrdinalIgnoreCase))
        {
            // The platform creates the Custom API's message (named after its unique name) with it, and the message
            // filter of its bound table, so a step can register on it.
            string? bound = entity.GetAttributeValue<string>("boundentitylogicalname");
            SeedMessage(entity.GetAttributeValue<string>("uniquename"), string.IsNullOrWhiteSpace(bound) ? [] : [bound!]);
        }

        return id;
    }

    public void Update(Entity entity)
    {
        Writes.Add("update:" + entity.LogicalName);
        Entity row = Existing(entity.LogicalName, entity.Id);
        foreach (KeyValuePair<string, object> attribute in entity.Attributes)
        {
            // Writing null clears the column: a Dataverse read no longer returns it.
            if (attribute.Value == null)
            {
                row.Attributes.Remove(attribute.Key);
            }
            else
            {
                row[attribute.Key] = attribute.Value;
            }
        }

        row["modifiedon"] = Tick();
        if (string.Equals(entity.LogicalName, "pluginpackage", StringComparison.OrdinalIgnoreCase) && entity.Contains("content"))
        {
            ImportPackage(row);
        }
    }

    public void Delete(string entityName, Guid id)
    {
        Writes.Add("delete:" + entityName);
        Existing(entityName, id);
        Table(entityName).Remove(id);
    }

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => Clone(Existing(entityName, id));

    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        QueryExpression expression = query as QueryExpression
            ?? throw new NotSupportedException($"StatefulOrgService only runs QueryExpression, not {query.GetType().Name}.");

        IEnumerable<Entity> rows = Table(expression.EntityName).Values
            .Where(row => Matches(row, expression.Criteria))
            .Select(row => Join(Clone(row), expression.LinkEntities))
            .Where(row => row != null)!;
        if (expression.TopCount is int top)
        {
            rows = rows.Take(top);
        }

        EntityCollection result = new();
        result.Entities.AddRange(rows);
        return result;
    }

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        if (_packageFiles.TryExecute(request, out OrganizationResponse fileResponse))
        {
            return fileResponse;
        }

        switch (request)
        {
            case ExecuteMultipleRequest batch:
                return RunBatch(batch);
            case CreateRequest create:
                return new CreateResponse { Results = { ["id"] = Create(create.Target) } };
            case UpdateRequest update:
                Update(update.Target);
                return new UpdateResponse();
            case DeleteRequest delete:
                Delete(delete.Target.LogicalName, delete.Target.Id);
                return new DeleteResponse();
            case SetStateRequest setState:
                Writes.Add("setstate:" + setState.EntityMoniker.LogicalName);
                Entity row = Existing(setState.EntityMoniker.LogicalName, setState.EntityMoniker.Id);
                row["statecode"] = setState.State;
                row["statuscode"] = setState.Status;
                return new SetStateResponse();
            default:
                throw new NotSupportedException($"StatefulOrgService does not run {request.GetType().Name} ({request.RequestName}).");
        }
    }

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        => throw new NotSupportedException();

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        => throw new NotSupportedException();

    private ExecuteMultipleResponse RunBatch(ExecuteMultipleRequest batch)
    {
        bool continueOnError = batch.Settings?.ContinueOnError ?? false;
        bool returnResponses = batch.Settings?.ReturnResponses ?? false;
        ExecuteMultipleResponseItemCollection items = new();
        bool faulted = false;
        for (int i = 0; i < batch.Requests.Count; i++)
        {
            try
            {
                OrganizationResponse response = Execute(batch.Requests[i]);
                if (returnResponses)
                {
                    items.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = response });
                }
            }
            catch (FaultException<OrganizationServiceFault> fault)
            {
                faulted = true;
                items.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Fault = fault.Detail });
                if (!continueOnError)
                {
                    break;
                }
            }
        }

        ExecuteMultipleResponse result = new();
        result.Results["IsFaulted"] = faulted;
        result.Results["Responses"] = items;
        return result;
    }

    // The platform side of a package import: the uploaded .nupkg moves to the stored `package` file and `content`
    // reads back empty (verified live); the package's assembly and plugin types appear (or are refreshed). A plugin type
    // the new content no longer has is deleted, and the import is refused, before any change, while such a type still has
    // a step (verified live: 0x80040265 "Unable to delete '<type>' plugintype due to N step(s)", even for a disabled
    // step). An assembly of the package the new content no longer has (a renamed assembly) is modelled the same way,
    // removed with its types: that part is NOT verified live.
    private void ImportPackage(Entity package)
    {
        string name = package.GetAttributeValue<string>("name");
        if (!_packageContents.TryGetValue(name, out (string AssemblyName, string[] TypeNames) contents))
        {
            throw Fault($"The plugin package '{name}' is not defined in this fake (call DefinePackage).");
        }

        Entity[] packageAssemblies = Table("pluginassembly").Values
            .Where(a => a.GetAttributeValue<EntityReference>("packageid")?.Id == package.Id)
            .ToArray();
        Entity[] droppedTypes = Table("plugintype").Values
            .Where(t => packageAssemblies.Any(a => a.Id == t.GetAttributeValue<EntityReference>("pluginassemblyid")?.Id))
            .Where(t =>
            {
                Entity assembly = packageAssemblies.First(a => a.Id == t.GetAttributeValue<EntityReference>("pluginassemblyid")!.Id);
                return !TextEquals(assembly.GetAttributeValue<string>("name"), contents.AssemblyName)
                    || !contents.TypeNames.Any(n => TextEquals(n, t.GetAttributeValue<string>("typename")));
            })
            .ToArray();
        foreach (Entity type in droppedTypes)
        {
            int steps = Table("sdkmessageprocessingstep").Values.Count(step => step.GetAttributeValue<EntityReference>("eventhandler")?.Id == type.Id);
            if (steps > 0)
            {
                throw new FaultException<OrganizationServiceFault>(
                    new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040265), Message = $"Unable to delete '{type.GetAttributeValue<string>("typename")}' plugintype due to {steps} step(s) registered." },
                    "plugintype has steps");
            }

            // Dataverse also refuses to delete a plugin type a Custom API references (the reason apply detaches a moved
            // Custom API first); the code and text here are the fake's, not captured live.
            if (Table("customapi").Values.Any(api => api.GetAttributeValue<EntityReference>("plugintypeid")?.Id == type.Id))
            {
                throw Fault($"Unable to delete '{type.GetAttributeValue<string>("typename")}' plugintype: a Custom API references it.");
            }
        }

        foreach (Entity type in droppedTypes)
        {
            Table("plugintype").Remove(type.Id);
        }

        foreach (Entity retired in packageAssemblies.Where(a => !TextEquals(a.GetAttributeValue<string>("name"), contents.AssemblyName)))
        {
            Table("pluginassembly").Remove(retired.Id);
        }

        _packageFiles.StoreUpload(package.Id, package);
        package["content"] = string.Empty;

        Entity? assembly = Table("pluginassembly").Values.FirstOrDefault(a => TextEquals(a.GetAttributeValue<string>("name"), contents.AssemblyName));
        Guid assemblyId = assembly?.Id ?? Insert(new Entity("pluginassembly")
        {
            ["name"] = contents.AssemblyName,
            ["packageid"] = new EntityReference("pluginpackage", package.Id),
        });

        foreach (string typeName in contents.TypeNames)
        {
            bool exists = Table("plugintype").Values.Any(t =>
                TextEquals(t.GetAttributeValue<string>("typename"), typeName)
                && t.GetAttributeValue<EntityReference>("pluginassemblyid")?.Id == assemblyId);
            if (!exists)
            {
                Insert(new Entity("plugintype")
                {
                    ["typename"] = typeName,
                    ["name"] = typeName,
                    ["assemblyname"] = contents.AssemblyName,
                    ["pluginassemblyid"] = new EntityReference("pluginassembly", assemblyId),
                });
            }
        }
    }

    private Guid Insert(Entity entity)
    {
        Guid id = entity.Id == Guid.Empty ? Guid.NewGuid() : entity.Id;
        Dictionary<Guid, Entity> table = Table(entity.LogicalName);
        if (table.ContainsKey(id))
        {
            throw Fault($"Cannot insert duplicate key: {entity.LogicalName} {id}.");
        }

        Entity row = Clone(entity);
        // Like a Dataverse read, a null column is simply absent from the row.
        foreach (string nullColumn in row.Attributes.Where(a => a.Value == null).Select(a => a.Key).ToArray())
        {
            row.Attributes.Remove(nullColumn);
        }

        row.Id = id;
        row[entity.LogicalName + "id"] = id;
        DateTime now = Tick();
        row["createdon"] = now;
        row["modifiedon"] = now;
        if (string.Equals(entity.LogicalName, "sdkmessageprocessingstep", StringComparison.OrdinalIgnoreCase) && !row.Contains("statecode"))
        {
            row["statecode"] = new OptionSetValue(0);
            row["statuscode"] = new OptionSetValue(1);
        }

        table[id] = row;
        return id;
    }

    private DateTime Tick() => _clock = _clock.AddSeconds(1);

    private Dictionary<Guid, Entity> Table(string entityName)
    {
        if (!_tables.TryGetValue(entityName, out Dictionary<Guid, Entity>? table))
        {
            table = new Dictionary<Guid, Entity>();
            _tables[entityName] = table;
        }

        return table;
    }

    private Entity Existing(string entityName, Guid id) =>
        Table(entityName).TryGetValue(id, out Entity? row) ? row : throw Fault($"{entityName} With Id = {id} Does Not Exist");

    private Entity? Join(Entity row, IEnumerable<LinkEntity> links)
    {
        foreach (LinkEntity link in links)
        {
            object? fromValue = Normalize(row.Contains(link.LinkFromAttributeName) ? row[link.LinkFromAttributeName] : null);
            Entity? target = fromValue == null
                ? null
                : Table(link.LinkToEntityName).Values.FirstOrDefault(candidate =>
                    ValueEquals(Normalize(candidate.Contains(link.LinkToAttributeName) ? candidate[link.LinkToAttributeName] : null), fromValue)
                    && Matches(candidate, link.LinkCriteria));
            if (target == null)
            {
                if (link.JoinOperator == JoinOperator.Inner)
                {
                    return null;
                }

                continue;
            }

            string alias = string.IsNullOrEmpty(link.EntityAlias) ? link.LinkToEntityName : link.EntityAlias;
            IEnumerable<string> columns = link.Columns.AllColumns ? target.Attributes.Keys : link.Columns.Columns;
            foreach (string column in columns)
            {
                if (target.Contains(column))
                {
                    row[alias + "." + column] = new AliasedValue(link.LinkToEntityName, column, target[column]);
                }
            }

            if (link.LinkEntities.Count > 0 && Join(row, link.LinkEntities) == null)
            {
                return null;
            }
        }

        return row;
    }

    private static bool Matches(Entity row, FilterExpression? filter)
    {
        if (filter == null)
        {
            return true;
        }

        IEnumerable<bool> results = filter.Conditions.Select(c => Matches(row, c))
            .Concat(filter.Filters.Select(f => Matches(row, f)));
        return filter.FilterOperator == LogicalOperator.Or ? !results.Any() || results.Any(r => r) : results.All(r => r);
    }

    private static bool Matches(Entity row, ConditionExpression condition)
    {
        object? actual = Normalize(row.Contains(condition.AttributeName) ? row[condition.AttributeName] : null);
        object?[] expected = condition.Values.SelectMany(Flatten).Select(Normalize).ToArray();
        return condition.Operator switch
        {
            ConditionOperator.Equal => ValueEquals(actual, Single(expected, condition)),
            ConditionOperator.NotEqual => !ValueEquals(actual, Single(expected, condition)),
            ConditionOperator.In => expected.Any(v => ValueEquals(actual, v)),
            ConditionOperator.NotIn => !expected.Any(v => ValueEquals(actual, v)),
            ConditionOperator.Null => actual == null,
            ConditionOperator.NotNull => actual != null,
            _ => throw new NotSupportedException($"StatefulOrgService does not evaluate the {condition.Operator} operator."),
        };
    }

    private static object? Single(object?[] values, ConditionExpression condition) =>
        values.Length == 1 ? values[0] : throw new NotSupportedException($"{condition.Operator} on '{condition.AttributeName}' takes exactly one value.");

    // AddCondition(..., In, someArray) can arrive as one array value rather than one value per element.
    private static IEnumerable<object?> Flatten(object? value) =>
        value is IEnumerable sequence and not string ? sequence.Cast<object?>() : [value];

    private static object? Normalize(object? value) => value switch
    {
        EntityReference reference => reference.Id,
        OptionSetValue option => option.Value,
        AliasedValue aliased => Normalize(aliased.Value),
        _ => value,
    };

    private static bool ValueEquals(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string a, string b) => TextEquals(a, b),
        _ => left.Equals(right),
    };

    private static bool TextEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Entity Clone(Entity source)
    {
        Entity copy = new(source.LogicalName, source.Id);
        foreach (KeyValuePair<string, object> attribute in source.Attributes)
        {
            copy[attribute.Key] = attribute.Value;
        }

        return copy;
    }

    private static FaultException<OrganizationServiceFault> Fault(string message) =>
        new(new OrganizationServiceFault { Message = message }, message);
}
