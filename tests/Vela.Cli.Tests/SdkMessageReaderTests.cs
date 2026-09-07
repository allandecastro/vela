using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
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
