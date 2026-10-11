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
// table/column names are as used here; each request has one sdkmessagepair per SOAP contract version and the
// field CLR types differ by pair (legacy crm/2007/WebServices carries null/pre-Xrm types; crm/2011/Contracts and
// xrm/7.1/Contracts carry the modern Microsoft.Xrm.Sdk.* types), and the pair order is not stable across
// environments - so the reader prefers the 2011/Contracts pair. A request is executed under its own name
// (SendEmail), not under the sdkmessage it runs as (Send). Request/response fields order by `position` and key by
// `name`.
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

    private static readonly Guid MessageId = Guid.NewGuid();
    private const string Modern = "http://schemas.microsoft.com/crm/2011/Contracts";

    // A sdkmessagerequest row joined to its pair (alias "pair"), as ReadSdkMessage queries it.
    private static Entity RequestRow(string name, string pairNamespace, string? table = null)
    {
        Entity row = Row("sdkmessagerequest",
            ("name", name),
            ("pair.namespace", new AliasedValue("sdkmessagepair", "namespace", pairNamespace)),
            ("pair.sdkmessageid", new AliasedValue("sdkmessagepair", "sdkmessageid", new EntityReference("sdkmessage", MessageId))));
        if (table != null)
        {
            row["primaryobjecttypecode"] = table;
        }

        return row;
    }

    // The ReadSdkMessage request lookup filters on the request name; ListRequestNames joins to the sdkmessage.
    private static bool IsRequestLookup(QueryExpression q) => q.Criteria.Conditions.Any(c => c.AttributeName == "name");

    private static FakeOrgService Service(Func<QueryExpression, EntityCollection?> route) => new FakeOrgService
    {
        OnRetrieveMultiple = q => route(q) ?? new EntityCollection(),
    };

    [Fact]
    public void ReadSdkMessage_MapsRequestAndResponseFields()
    {
        var svc = Service(q => q.EntityName switch
        {
            "sdkmessagerequest" => FakeOrgService.Collection(RequestRow("QualifyLead", Modern)),
            "sdkmessagerequestfield" => FakeOrgService.Collection(
                Row("sdkmessagerequestfield", ("name", "CreateAccount"), ("clrparser", "System.Boolean" + Cor), ("position", 1)),
                Row("sdkmessagerequestfield", ("name", "LeadId"), ("clrparser", "Microsoft.Xrm.Sdk.EntityReference" + Asm), ("position", 0))),
            "sdkmessageresponse" => FakeOrgService.Collection(Row("sdkmessageresponse")),
            "sdkmessageresponsefield" => FakeOrgService.Collection(
                Row("sdkmessageresponsefield", ("name", "CreatedEntities"), ("clrformatter", "Microsoft.Xrm.Sdk.EntityCollection" + Asm), ("position", 0))),
            _ => null,
        });

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "QualifyLead");

        Assert.Equal("QualifyLead", message.Name);

        // Ordered by position: LeadId (0) before CreateAccount (1).
        Assert.Equal(new[] { "LeadId", "CreateAccount" }, message.RequestFields.Select(f => f.Name).ToArray());
        Assert.Equal("EntityReference", message.RequestFields[0].CSharpType);
        Assert.Equal("bool", message.RequestFields[1].CSharpType);

        SdkMessageField response = Assert.Single(message.ResponseFields);
        Assert.Equal("CreatedEntities", response.Name);
        Assert.Equal("EntityCollection", response.CSharpType);

        // The request is looked up by its own name, joined to its pair.
        QueryExpression lookup = svc.Queries.First(q => q.EntityName == "sdkmessagerequest");
        Assert.Equal("QualifyLead", lookup.Criteria.Conditions.Single(c => c.AttributeName == "name").Values.Single());
        Assert.Equal("sdkmessagepair", lookup.LinkEntities.Single().LinkToEntityName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadSdkMessage_PrefersTheModernContractsPair_RegardlessOfOrder(bool with2011)
    {
        // Legacy pair listed first (as happens in some environments); 2011/Contracts wins, else xrm/*/Contracts.
        Entity legacy = RequestRow("Whatever", "http://schemas.microsoft.com/crm/2007/WebServices");
        Entity xrm = RequestRow("Whatever", "http://schemas.microsoft.com/xrm/7.1/Contracts");
        Entity modern = RequestRow("Whatever", Modern);
        Guid usedRequestId = Guid.Empty;

        var svc = Service(q =>
        {
            switch (q.EntityName)
            {
                case "sdkmessagerequest":
                    return with2011 ? FakeOrgService.Collection(legacy, xrm, modern) : FakeOrgService.Collection(legacy, xrm);
                case "sdkmessagerequestfield":
                    usedRequestId = (Guid)q.Criteria.Conditions.Single(c => c.AttributeName == "sdkmessagerequestid").Values[0];
                    return null;
                default:
                    return null;
            }
        });

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "Whatever");

        Assert.Equal("Whatever", message.Name);
        Assert.Equal(with2011 ? modern.Id : xrm.Id, usedRequestId);
        Assert.Empty(message.ResponseFields);
    }

    [Fact]
    public void ReadSdkMessage_DedupesAndSkipsBlankRequestFields()
    {
        var svc = Service(q => q.EntityName switch
        {
            "sdkmessagerequest" => FakeOrgService.Collection(RequestRow("M", Modern)),
            "sdkmessagerequestfield" => FakeOrgService.Collection(
                Row("sdkmessagerequestfield", ("name", "Dup"), ("clrparser", "System.String" + Cor), ("position", 1)),
                Row("sdkmessagerequestfield", ("name", "Dup"), ("clrparser", "System.String" + Cor), ("position", 2)),
                Row("sdkmessagerequestfield", ("name", "  "), ("clrparser", "System.String" + Cor), ("position", 0))),
            _ => null,
        });

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
    public void ReadSdkMessage_PopulatesApplicableEntities_FromSdkMessageFilter_DedupedAndOrdered()
    {
        var svc = Service(q => q.EntityName switch
        {
            "sdkmessagerequest" => FakeOrgService.Collection(RequestRow("M", Modern)),
            // Two filter rows for 'lead' (deduped), one for 'account', one blank (skipped), one "none"
            // (the global sentinel - skipped so the message falls into the generic On<TEntity>() path).
            "sdkmessagefilter" => FakeOrgService.Collection(
                Row("sdkmessagefilter", ("primaryobjecttypecode", "lead")),
                Row("sdkmessagefilter", ("primaryobjecttypecode", "account")),
                Row("sdkmessagefilter", ("primaryobjecttypecode", "lead")),
                Row("sdkmessagefilter", ("primaryobjecttypecode", "none")),
                Row("sdkmessagefilter", ("primaryobjecttypecode", "  "))),
            _ => null,
        });

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "M");

        Assert.Equal(new[] { "account", "lead" }, message.Entities.ToArray());

        // The filter query carries the request's sdkmessageid plus the step-eligibility conditions (mirrors the
        // ALL-mode list).
        QueryExpression filterQuery = svc.Queries.Single(q => q.EntityName == "sdkmessagefilter");
        Assert.Equal(MessageId, filterQuery.Criteria.Conditions.Single(c => c.AttributeName == "sdkmessageid").Values.Single());
        Assert.Equal(true, filterQuery.Criteria.Conditions.Single(c => c.AttributeName == "iscustomprocessingstepallowed").Values.Single());
        Assert.Equal(true, filterQuery.Criteria.Conditions.Single(c => c.AttributeName == "isvisible").Values.Single());
    }

    [Fact]
    public void ReadSdkMessage_RequestBoundToATable_AppliesToThatTableOnly()
    {
        // WinQuote runs as the Win sdkmessage, whose filters are opportunity and quote; the request is quote's.
        var svc = Service(q => q.EntityName switch
        {
            "sdkmessagerequest" => FakeOrgService.Collection(RequestRow("WinQuote", Modern, table: "quote")),
            "sdkmessagefilter" => FakeOrgService.Collection(
                Row("sdkmessagefilter", ("primaryobjecttypecode", "opportunity")),
                Row("sdkmessagefilter", ("primaryobjecttypecode", "quote"))),
            _ => null,
        });

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "WinQuote");

        Assert.Equal("WinQuote", message.Name);
        Assert.Equal(new[] { "quote" }, message.Entities.ToArray());
    }

    [Fact]
    public void ReadSdkMessage_NoSdkMessageFilter_YieldsEmptyEntities()
    {
        var svc = Service(q => q.EntityName == "sdkmessagerequest" ? FakeOrgService.Collection(RequestRow("M", Modern)) : null);

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "M");

        Assert.Empty(message.Entities);
    }

    [Fact]
    public void ReadSdkMessage_UnknownRequest_Throws()
    {
        var svc = new FakeOrgService { OnRetrieveMultiple = _ => new EntityCollection() };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SdkMessageReader.ReadSdkMessage(svc, "Nope"));
        Assert.Contains("SDK request 'Nope' was not found", ex.Message);
    }

    // "Send" is the sdkmessage, which Dataverse does not execute ("Request not supported", verified live): the reader
    // refuses it and names the requests to generate instead, rather than generating several classes.
    [Fact]
    public void ReadSdkMessage_AnSdkMessageName_IsRejected_WithTheRequestNames()
    {
        var svc = Service(q => q.EntityName == "sdkmessagerequest" && !IsRequestLookup(q)
            ? FakeOrgService.Collection(
                Row("sdkmessagerequest", ("name", "SendTemplate")),
                Row("sdkmessagerequest", ("name", "SendEmail")),
                Row("sdkmessagerequest", ("name", "SendEmail")),
                Row("sdkmessagerequest", ("name", "SendFax")))
            : null);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SdkMessageReader.ReadSdkMessage(svc, "Send"));
        Assert.Contains("'Send' is the sdkmessage a step registers on, not a request Dataverse executes. Generate SendEmail, SendFax, SendTemplate instead.", ex.Message);
    }

    [Fact]
    public void ListRequestNames_ReturnsTheRequestsOfTheMessage_SkippingPlaceholders()
    {
        var svc = Service(q => q.EntityName == "sdkmessagerequest"
            ? FakeOrgService.Collection(
                Row("sdkmessagerequest", ("name", "SetState{Entity.PrimaryEntityName}")),
                Row("sdkmessagerequest", ("name", "SetState")))
            : null);

        Assert.Equal(new[] { "SetState" }, SdkMessageReader.ListRequestNames(svc, "SetState").ToArray());

        LinkEntity message = svc.Queries.Single().LinkEntities.Single().LinkEntities.Single();
        Assert.Equal("sdkmessage", message.LinkToEntityName);
        Assert.Equal("SetState", message.LinkCriteria.Conditions.Single(c => c.AttributeName == "name").Values.Single());
    }

    [Fact]
    public void ReadSdkMessage_NoResponse_YieldsEmptyResponseFields()
    {
        var svc = Service(q => q.EntityName == "sdkmessagerequest" ? FakeOrgService.Collection(RequestRow("M", Modern)) : null);

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "M");

        Assert.Empty(message.RequestFields);
        Assert.Empty(message.ResponseFields);
    }
}
