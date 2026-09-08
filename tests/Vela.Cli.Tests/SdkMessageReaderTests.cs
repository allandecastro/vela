using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

// NOTE: the sdkmessage* schema was validated live against two environments. Findings baked into these tests:
// table/column names are as used here; a message has one pair per SOAP contract version and the field CLR types
// differ by pair (legacy crm/2007/WebServices carries null/pre-Xrm types; crm/2011/Contracts and
// xrm/7.1/Contracts carry the modern Microsoft.Xrm.Sdk.* types), and the pair order is not stable across
// environments - so the reader prefers the 2011/Contracts pair. Request/response fields order by `position`
// and key by `name`.
public class SdkMessageReaderTests
{
    private const string Asm = ", Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    private const string Cor = ", mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

    private static Entity Row(string logicalName, params (string Key, object Value)[] attrs)
    {
        Entity e = new Entity(logicalName, Guid.NewGuid());
        foreach ((string key, object value) in attrs)
        {
            e[key] = value;
        }

        return e;
    }

    [Fact]
    public void ReadSdkMessage_MapsRequestAndResponseFields()
    {
        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessage" => FakeOrgService.Collection(Row("sdkmessage", ("name", "QualifyLead"))),
                "sdkmessagepair" => FakeOrgService.Collection(Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/crm/2011/Contracts"))),
                "sdkmessagerequest" => FakeOrgService.Collection(Row("sdkmessagerequest")),
                "sdkmessagerequestfield" => FakeOrgService.Collection(
                    Row("sdkmessagerequestfield", ("name", "CreateAccount"), ("clrparser", "System.Boolean" + Cor), ("position", 1)),
                    Row("sdkmessagerequestfield", ("name", "LeadId"), ("clrparser", "Microsoft.Xrm.Sdk.EntityReference" + Asm), ("position", 0))),
                "sdkmessageresponse" => FakeOrgService.Collection(Row("sdkmessageresponse")),
                "sdkmessageresponsefield" => FakeOrgService.Collection(
                    Row("sdkmessageresponsefield", ("name", "CreatedEntities"), ("clrformatter", "Microsoft.Xrm.Sdk.EntityCollection" + Asm), ("position", 0))),
                _ => new EntityCollection(),
            },
        };

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "QualifyLead");

        Assert.Equal("QualifyLead", message.Name);

        // Ordered by position: LeadId (0) before CreateAccount (1).
        Assert.Equal(new[] { "LeadId", "CreateAccount" }, message.RequestFields.Select(f => f.Name).ToArray());
        Assert.Equal("EntityReference", message.RequestFields[0].CSharpType);
        Assert.Equal("bool", message.RequestFields[1].CSharpType);

        SdkMessageField response = Assert.Single(message.ResponseFields);
        Assert.Equal("CreatedEntities", response.Name);
        Assert.Equal("EntityCollection", response.CSharpType);
    }

    [Fact]
    public void ReadSdkMessage_Prefers2011ContractsPair_OverLegacyAndRegardlessOfOrder()
    {
        // Legacy pair listed first (as happens in some environments); the modern 2011/Contracts pair must win.
        Entity legacy = Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/crm/2007/WebServices"));
        Entity modern = Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/crm/2011/Contracts"));
        Entity xrm = Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/xrm/7.1/Contracts"));
        Guid usedPairId = Guid.Empty;

        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                switch (q.EntityName)
                {
                    case "sdkmessage":
                        return FakeOrgService.Collection(Row("sdkmessage", ("name", "Whatever")));
                    case "sdkmessagepair":
                        return FakeOrgService.Collection(legacy, xrm, modern);
                    case "sdkmessagerequest":
                        // Capture which pair id the request query filtered on.
                        usedPairId = (Guid)q.Criteria.Conditions.Single(c => c.AttributeName == "sdkmessagepairid").Values[0];
                        return FakeOrgService.Collection(Row("sdkmessagerequest"));
                    default:
                        return new EntityCollection();
                }
            },
        };

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "Whatever");

        Assert.Equal("Whatever", message.Name);
        Assert.Equal(modern.Id, usedPairId);
        Assert.Empty(message.ResponseFields);
    }

    [Fact]
    public void ReadSdkMessage_FallsBackToContractsPair_WhenNo2011()
    {
        // No 2011/Contracts present: an xrm/*/Contracts pair is preferred over the legacy WebServices pair.
        Entity legacy = Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/crm/2007/WebServices"));
        Entity xrm = Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/xrm/7.1/Contracts"));
        Guid usedPairId = Guid.Empty;

        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                switch (q.EntityName)
                {
                    case "sdkmessage":
                        return FakeOrgService.Collection(Row("sdkmessage", ("name", "Whatever")));
                    case "sdkmessagepair":
                        return FakeOrgService.Collection(legacy, xrm);
                    case "sdkmessagerequest":
                        usedPairId = (Guid)q.Criteria.Conditions.Single(c => c.AttributeName == "sdkmessagepairid").Values[0];
                        return FakeOrgService.Collection(Row("sdkmessagerequest"));
                    default:
                        return new EntityCollection();
                }
            },
        };

        SdkMessageReader.ReadSdkMessage(svc, "Whatever");

        Assert.Equal(xrm.Id, usedPairId);
    }

    [Fact]
    public void ReadSdkMessage_DedupesAndSkipsBlankRequestFields()
    {
        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessage" => FakeOrgService.Collection(Row("sdkmessage", ("name", "M"))),
                "sdkmessagepair" => FakeOrgService.Collection(Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/crm/2011/Contracts"))),
                "sdkmessagerequest" => FakeOrgService.Collection(Row("sdkmessagerequest")),
                "sdkmessagerequestfield" => FakeOrgService.Collection(
                    Row("sdkmessagerequestfield", ("name", "Dup"), ("clrparser", "System.String" + Cor), ("position", 1)),
                    Row("sdkmessagerequestfield", ("name", "Dup"), ("clrparser", "System.String" + Cor), ("position", 2)),
                    Row("sdkmessagerequestfield", ("name", "  "), ("clrparser", "System.String" + Cor), ("position", 0))),
                _ => new EntityCollection(),
            },
        };

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "M");

        SdkMessageField only = Assert.Single(message.RequestFields);
        Assert.Equal("Dup", only.Name);
        Assert.Equal("string", only.CSharpType);
    }

    // Simulates a joined sdkmessage row: the name plus the aliased primaryobjecttypecode from the linked filter.
    private static Entity JoinedRow(string name, string entity) =>
        Row("sdkmessage", ("name", name), ("filter.primaryobjecttypecode", new AliasedValue("sdkmessagefilter", "primaryobjecttypecode", entity)));

    [Fact]
    public void ListGeneratableMessages_FiltersPrivate_ExcludesCustomApis_AggregatesEntities_OrdersAndDedupes()
    {
        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                // The inner join to sdkmessagefilter yields one row per (message, entity). "Whatever" applies to two
                // entities; "zava_Api" matches a customapi uniquename and must be dropped.
                "sdkmessage" => FakeOrgService.Collection(
                    JoinedRow("Whatever", "account"),
                    JoinedRow("AddToQueue", "queue"),
                    JoinedRow("zava_Api", "account"),
                    JoinedRow("Whatever", "contact")),
                "customapi" => FakeOrgService.Collection(Row("customapi", ("uniquename", "zava_Api"))),
                _ => new EntityCollection(),
            },
        };

        IReadOnlyList<GeneratableMessage> messages = SdkMessageReader.ListGeneratableMessages(svc);

        // Custom-API-backed "zava_Api" excluded; grouped by name; ordered.
        Assert.Equal(new[] { "AddToQueue", "Whatever" }, messages.Select(m => m.Name).ToArray());
        // Entities aggregated across the joined rows, deduped and ordered.
        Assert.Equal(new[] { "account", "contact" }, messages.Single(m => m.Name == "Whatever").Entities.ToArray());
        Assert.Equal(new[] { "queue" }, messages.Single(m => m.Name == "AddToQueue").Entities.ToArray());

        // The message query is the Event Framework "available events" shape: distinct, non-private, the deprecated
        // name exclusions, and an inner join to sdkmessagefilter requiring a visible, step-allowed filter.
        QueryExpression messageQuery = svc.Queries.Single(q => q.EntityName == "sdkmessage");
        Assert.True(messageQuery.Distinct);
        ConditionExpression isPrivate = messageQuery.Criteria.Conditions.Single(c => c.AttributeName == "isprivate");
        Assert.Equal(ConditionOperator.Equal, isPrivate.Operator);
        Assert.Equal(false, isPrivate.Values.Single());
        ConditionExpression nameExclusions = messageQuery.Criteria.Conditions.Single(c => c.AttributeName == "name");
        Assert.Equal(ConditionOperator.NotIn, nameExclusions.Operator);
        Assert.Contains("Execute", nameExclusions.Values.Cast<string>());
        Assert.Contains("SetStateDynamicEntity", nameExclusions.Values.Cast<string>());

        LinkEntity filter = messageQuery.LinkEntities.Single();
        Assert.Equal("sdkmessagefilter", filter.LinkToEntityName);
        Assert.Equal(JoinOperator.Inner, filter.JoinOperator);
        Assert.Equal(true, filter.LinkCriteria.Conditions.Single(c => c.AttributeName == "iscustomprocessingstepallowed").Values.Single());
        Assert.Equal(true, filter.LinkCriteria.Conditions.Single(c => c.AttributeName == "isvisible").Values.Single());
    }

    [Fact]
    public void ListGeneratableMessages_NoCustomApis_ReturnsAllEligible()
    {
        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessage" => FakeOrgService.Collection(
                    JoinedRow("AddToQueue", "queue"),
                    JoinedRow("RouteTo", "queue")),
                _ => new EntityCollection(),
            },
        };

        IReadOnlyList<GeneratableMessage> messages = SdkMessageReader.ListGeneratableMessages(svc);

        Assert.Equal(new[] { "AddToQueue", "RouteTo" }, messages.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void ReadSdkMessage_UnknownMessage_Throws()
    {
        var svc = new FakeOrgService { OnRetrieveMultiple = _ => new EntityCollection() };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SdkMessageReader.ReadSdkMessage(svc, "Nope"));
        Assert.Contains("Nope", ex.Message);
    }

    [Fact]
    public void ReadSdkMessage_NoResponse_YieldsEmptyResponseFields()
    {
        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessage" => FakeOrgService.Collection(Row("sdkmessage", ("name", "M"))),
                "sdkmessagepair" => FakeOrgService.Collection(Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/crm/2011/Contracts"))),
                "sdkmessagerequest" => FakeOrgService.Collection(Row("sdkmessagerequest")),
                _ => new EntityCollection(),
            },
        };

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "M");

        Assert.Empty(message.RequestFields);
        Assert.Empty(message.ResponseFields);
    }
}
