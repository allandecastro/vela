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

    // The single contract behind a message that serves one request.
    private static SdkMessage ReadSingle(FakeOrgService svc, string name) => Assert.Single(SdkMessageReader.ReadSdkMessages(svc, name));

    // A request row per pair whose id IS the pair id, so a field query on the request tells which pair won.
    private static Entity RequestOfPair(QueryExpression q) =>
        new Entity("sdkmessagerequest", (Guid)q.Criteria.Conditions.Single(c => c.AttributeName == "sdkmessagepairid").Values[0]);

    private static Guid PairOfRequest(QueryExpression q) =>
        (Guid)q.Criteria.Conditions.Single(c => c.AttributeName == "sdkmessagerequestid").Values[0];

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

        SdkMessage message = ReadSingle(svc, "QualifyLead");

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
                        return FakeOrgService.Collection(RequestOfPair(q));
                    case "sdkmessagerequestfield":
                        // Capture which pair's request the fields were read from.
                        usedPairId = PairOfRequest(q);
                        return new EntityCollection();
                    default:
                        return new EntityCollection();
                }
            },
        };

        SdkMessage message = ReadSingle(svc, "Whatever");

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
                        return FakeOrgService.Collection(RequestOfPair(q));
                    case "sdkmessagerequestfield":
                        // Capture which pair's request the fields were read from.
                        usedPairId = PairOfRequest(q);
                        return new EntityCollection();
                    default:
                        return new EntityCollection();
                }
            },
        };

        ReadSingle(svc, "Whatever");

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

        SdkMessage message = ReadSingle(svc, "M");

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
        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessage" => FakeOrgService.Collection(Row("sdkmessage", ("name", "QualifyLead"))),
                "sdkmessagepair" => FakeOrgService.Collection(Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/crm/2011/Contracts"))),
                "sdkmessagerequest" => FakeOrgService.Collection(Row("sdkmessagerequest")),
                // Two filter rows for 'lead' (deduped), one for 'account', one blank (skipped), one "none"
                // (the global sentinel - skipped so the message falls into the generic On<TEntity>() path).
                "sdkmessagefilter" => FakeOrgService.Collection(
                    Row("sdkmessagefilter", ("primaryobjecttypecode", "lead")),
                    Row("sdkmessagefilter", ("primaryobjecttypecode", "account")),
                    Row("sdkmessagefilter", ("primaryobjecttypecode", "lead")),
                    Row("sdkmessagefilter", ("primaryobjecttypecode", "none")),
                    Row("sdkmessagefilter", ("primaryobjecttypecode", "  "))),
                _ => new EntityCollection(),
            },
        };

        SdkMessage message = ReadSingle(svc, "QualifyLead");

        Assert.Equal(new[] { "account", "lead" }, message.Entities.ToArray());

        // The filter query carries the sdkmessageid plus the step-eligibility conditions (mirrors the ALL-mode list).
        QueryExpression filterQuery = svc.Queries.Single(q => q.EntityName == "sdkmessagefilter");
        Assert.Contains(filterQuery.Criteria.Conditions, c => c.AttributeName == "sdkmessageid");
        Assert.Equal(true, filterQuery.Criteria.Conditions.Single(c => c.AttributeName == "iscustomprocessingstepallowed").Values.Single());
        Assert.Equal(true, filterQuery.Criteria.Conditions.Single(c => c.AttributeName == "isvisible").Values.Single());
    }

    [Fact]
    public void ReadSdkMessage_NoSdkMessageFilter_YieldsEmptyEntities()
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

        SdkMessage message = ReadSingle(svc, "M");

        Assert.Empty(message.Entities);
    }

    [Fact]
    public void ReadSdkMessage_UnknownMessage_Throws()
    {
        var svc = new FakeOrgService { OnRetrieveMultiple = _ => new EntityCollection() };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SdkMessageReader.ReadSdkMessages(svc, "Nope"));
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

        SdkMessage message = ReadSingle(svc, "M");

        Assert.Empty(message.RequestFields);
        Assert.Empty(message.ResponseFields);
    }

    // Verified live 2026-10-09: the Close sdkmessage has one request per entity, each with its own fields, on its
    // own pairs (CloseIncident: incident + IncidentResolution; CloseQuote: quote + QuoteClose).
    private static FakeOrgService SharedCloseMessage()
    {
        Guid incident2011 = Guid.NewGuid(), incidentLegacy = Guid.NewGuid(), quote2011 = Guid.NewGuid();
        return new FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                switch (q.EntityName)
                {
                    case "sdkmessage":
                        // A request-name lookup joins through the pair to the request; both resolve to Close.
                        bool byName = q.Criteria.Conditions.Any(c => c.AttributeName == "name" && (string)c.Values[0] == "Close");
                        bool byRequest = q.LinkEntities.Any();
                        return byName || byRequest ? FakeOrgService.Collection(Row("sdkmessage", ("name", "Close"))) : new EntityCollection();
                    case "sdkmessagepair":
                        return FakeOrgService.Collection(
                            new Entity("sdkmessagepair", incidentLegacy) { ["namespace"] = "http://schemas.microsoft.com/crm/2007/WebServices" },
                            new Entity("sdkmessagepair", incident2011) { ["namespace"] = "http://schemas.microsoft.com/crm/2011/Contracts" },
                            new Entity("sdkmessagepair", quote2011) { ["namespace"] = "http://schemas.microsoft.com/crm/2011/Contracts" });
                    case "sdkmessagerequest":
                        Entity request = RequestOfPair(q);
                        bool isQuote = request.Id == quote2011;
                        request["name"] = isQuote ? "CloseQuote" : "CloseIncident";
                        request["primaryobjecttypecode"] = request.Id == incidentLegacy ? null : (isQuote ? "quote" : "incident");
                        return FakeOrgService.Collection(request);
                    case "sdkmessagerequestfield":
                        Guid pair = PairOfRequest(q);
                        return pair == quote2011
                            ? FakeOrgService.Collection(Row("sdkmessagerequestfield", ("name", "QuoteClose"), ("clrparser", "Microsoft.Xrm.Sdk.Entity" + Asm), ("position", 0)))
                            : pair == incident2011
                                ? FakeOrgService.Collection(Row("sdkmessagerequestfield", ("name", "IncidentResolution"), ("clrparser", "Microsoft.Xrm.Sdk.Entity" + Asm), ("position", 0)))
                                : FakeOrgService.Collection(Row("sdkmessagerequestfield", ("name", "Legacy"), ("clrparser", null!), ("position", 0)));
                    case "sdkmessagefilter":
                        return FakeOrgService.Collection(
                            Row("sdkmessagefilter", ("primaryobjecttypecode", "incident")),
                            Row("sdkmessagefilter", ("primaryobjecttypecode", "quote")));
                    default:
                        return new EntityCollection();
                }
            },
        };
    }

    [Fact]
    public void ReadSdkMessages_SharedMessage_YieldsOneContractPerRequest_WithItsOwnFieldsAndEntity()
    {
        IReadOnlyList<SdkMessage> contracts = SdkMessageReader.ReadSdkMessages(SharedCloseMessage(), "Close");

        Assert.Equal(new[] { "CloseIncident", "CloseQuote" }, contracts.Select(c => c.Name).ToArray());
        Assert.All(contracts, c => Assert.Equal("Close", c.MessageName));

        SdkMessage incident = contracts[0];
        Assert.Equal("IncidentResolution", Assert.Single(incident.RequestFields).Name); // the 2011 pair, not the legacy one
        Assert.Equal(new[] { "incident" }, incident.Entities.ToArray());

        SdkMessage quote = contracts[1];
        Assert.Equal("QuoteClose", Assert.Single(quote.RequestFields).Name);
        Assert.Equal(new[] { "quote" }, quote.Entities.ToArray());
    }

    [Fact]
    public void ReadSdkMessages_RequestName_YieldsJustThatRequest_OnItsSdkMessage()
    {
        SdkMessage quote = Assert.Single(SdkMessageReader.ReadSdkMessages(SharedCloseMessage(), "CloseQuote"));

        Assert.Equal("CloseQuote", quote.Name);
        Assert.Equal("Close", quote.MessageName);
        Assert.Equal("QuoteClose", Assert.Single(quote.RequestFields).Name);
    }

    [Fact]
    public void ReadSdkMessages_UnnamedRequest_IsNamedAfterItsMessage()
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

        SdkMessage message = ReadSingle(svc, "M");

        Assert.Equal("M", message.Name);
        Assert.Equal("M", message.MessageName);
    }
}
