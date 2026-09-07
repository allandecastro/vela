using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

// NOTE: the sdkmessage* schema (table/column names, the Organization-endpoint heuristic, the response-field
// ordering column) cannot be integration-tested here (no Dataverse). These tests pin the reader's mapping and
// query shaping against canned metadata; the live schema still needs validation against a real environment.
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
                "sdkmessagepair" => FakeOrgService.Collection(Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/.../Organization"))),
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
    public void ReadSdkMessage_PrefersOrganizationEndpointPair()
    {
        Entity other = Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/.../Discovery"));
        Entity org = Row("sdkmessagepair", ("namespace", "http://schemas.microsoft.com/.../Organization"));
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
                        return FakeOrgService.Collection(other, org);
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
        Assert.Equal(org.Id, usedPairId);
        Assert.Empty(message.ResponseFields);
    }

    [Fact]
    public void ReadSdkMessage_DedupesAndSkipsBlankRequestFields()
    {
        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessage" => FakeOrgService.Collection(Row("sdkmessage", ("name", "M"))),
                "sdkmessagepair" => FakeOrgService.Collection(Row("sdkmessagepair", ("namespace", "Organization"))),
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
                "sdkmessagepair" => FakeOrgService.Collection(Row("sdkmessagepair", ("namespace", "Organization"))),
                "sdkmessagerequest" => FakeOrgService.Collection(Row("sdkmessagerequest")),
                _ => new EntityCollection(),
            },
        };

        SdkMessage message = SdkMessageReader.ReadSdkMessage(svc, "M");

        Assert.Empty(message.RequestFields);
        Assert.Empty(message.ResponseFields);
    }
}
