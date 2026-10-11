using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Context;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Pins the TracingOrganizationService behaviours the mutation run (#448) left unchecked: the Execute trace (named and
// null requests), FetchXML redaction of a null query and of single-quoted values, and the early-bound downcast - what
// the copy keeps, which collections and requests are copied, and which ones are passed through untouched.
public sealed class TracingOrganizationServiceMutationTests
{
    private static (TracingOrganizationService Svc, Mock<IOrganizationService> Inner, List<string> Lines) Build()
    {
        List<string> lines = new List<string>();
        Mock<ILocalPluginContext> ctx = new Mock<ILocalPluginContext>();
        ctx.SetupGet(c => c.Depth).Returns(2);
        ctx.Setup(c => c.Trace(It.IsAny<string>(), It.IsAny<object[]>()))
            .Callback<string, object[]>((format, args) => lines.Add(string.Format(format, args)));
        Mock<IOrganizationService> inner = new Mock<IOrganizationService>();
        return (new TracingOrganizationService(inner.Object, ctx.Object, "S"), inner, lines);
    }

    // The entity the inner service received from Update.
    private static Entity UpdateAndCapture(Entity entity)
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner, _) = Build();
        Entity? captured = null;
        inner.Setup(s => s.Update(It.IsAny<Entity>())).Callback<Entity>(e => captured = e);
        svc.Update(entity);
        return captured!;
    }

    // The request the inner service received from Execute.
    private static OrganizationRequest ExecuteAndCapture(OrganizationRequest request)
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner, _) = Build();
        OrganizationRequest? captured = null;
        inner.Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Callback<OrganizationRequest>(r => captured = r)
            .Returns(new OrganizationResponse());
        svc.Execute(request);
        return captured!;
    }

    // No public parameterless constructor: the sanitized copy falls back to a base OrganizationRequest.
    private sealed class NamedOnlyRequest : OrganizationRequest
    {
        public NamedOnlyRequest(string name)
        {
            RequestName = name;
        }
    }

    [Fact]
    public void Execute_traces_the_request_name()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner, List<string> lines) = Build();
        OrganizationResponse response = new OrganizationResponse();
        inner.Setup(s => s.Execute(It.IsAny<OrganizationRequest>())).Returns(response);

        Assert.Same(response, svc.Execute(new OrganizationRequest("WhoAmI")));

        Assert.Equal(2, lines.Count);
        Assert.Equal("Dataverse.Execute start scope=S depth=2 request=WhoAmI", lines[0]);
        Assert.Matches(new Regex("^Dataverse\\.Execute ok \\d+ ms$"), lines[1]);
    }

    [Fact]
    public void Execute_with_a_null_request_traces_an_unknown_request_and_still_delegates()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner, List<string> lines) = Build();
        OrganizationResponse response = new OrganizationResponse();
        inner.Setup(s => s.Execute(null!)).Returns(response);

        Assert.Same(response, svc.Execute(null!));

        Assert.Equal("Dataverse.Execute start scope=S depth=2 request=UnknownRequest", lines[0]);
        inner.Verify(s => s.Execute(null!), Times.Once);
    }

    [Fact]
    public void Redacting_a_null_fetch_returns_an_empty_string()
    {
        Assert.Equal(string.Empty, TracingOrganizationService.RedactFetchXmlValues(null));
    }

    [Fact]
    public void Redacting_masks_single_quoted_values()
    {
        string redacted = TracingOrganizationService.RedactFetchXmlValues(
            "<condition attribute='emailaddress1' operator='eq' value = 'jane@contoso.com' />");

        Assert.Equal("<condition attribute='emailaddress1' operator='eq' value='***' />", redacted);
    }

    [Fact]
    public void The_downcast_copy_keeps_the_record_metadata()
    {
        KeyAttributeCollection keys = new KeyAttributeCollection { ["accountnumber"] = "A-1" };
        TestAccount account = new TestAccount
        {
            Id = Guid.NewGuid(),
            Name = "Zava",
            EntityState = EntityState.Changed,
            RowVersion = "42",
            KeyAttributes = keys,
        };
        account.FormattedValues["statecode"] = "Active";

        Entity captured = UpdateAndCapture(account);

        Assert.Equal(typeof(Entity), captured.GetType());
        Assert.Equal(account.Id, captured.Id);
        Assert.Equal(EntityState.Changed, captured.EntityState);
        Assert.Equal("42", captured.RowVersion);
        Assert.Same(keys, captured.KeyAttributes);
        Assert.Equal("Active", captured.FormattedValues["statecode"]);
        Assert.Equal("Zava", captured.GetAttributeValue<string>("name"));
    }

    [Fact]
    public void A_collection_without_early_bound_rows_is_shared_not_copied()
    {
        EntityCollection parties = new EntityCollection(new List<Entity> { new Entity("activityparty") });
        TestAccount account = new TestAccount { Name = "Zava" };
        account["vela_parties"] = parties;

        Entity captured = UpdateAndCapture(account);

        Assert.Equal(typeof(Entity), captured.GetType());
        Assert.Same(parties, captured["vela_parties"]);
    }

    [Fact]
    public void A_collection_with_one_early_bound_row_among_others_is_copied_with_its_paging_state()
    {
        Entity plain = new Entity("account");
        TestAccount early = new TestAccount { Name = "Early" };
        EntityCollection rows = new EntityCollection(new List<Entity> { plain, early })
        {
            EntityName = "account",
            MoreRecords = true,
            PagingCookie = "cookie",
            MinActiveRowVersion = "7",
            TotalRecordCount = 12,
            TotalRecordCountLimitExceeded = true,
        };
        TestAccount owner = new TestAccount();
        owner["vela_rows"] = rows;

        EntityCollection copied = UpdateAndCapture(owner).GetAttributeValue<EntityCollection>("vela_rows");

        Assert.NotSame(rows, copied);
        Assert.Equal(new[] { typeof(Entity), typeof(Entity) }, new[] { copied.Entities[0].GetType(), copied.Entities[1].GetType() });
        Assert.Same(plain, copied.Entities[0]);
        Assert.Equal("Early", copied.Entities[1].GetAttributeValue<string>("name"));
        Assert.Equal("account", copied.EntityName);
        Assert.True(copied.MoreRecords);
        Assert.Equal("cookie", copied.PagingCookie);
        Assert.Equal("7", copied.MinActiveRowVersion);
        Assert.Equal(12, copied.TotalRecordCount);
        Assert.True(copied.TotalRecordCountLimitExceeded);
        Assert.IsType<TestAccount>(rows.Entities[1]);
    }

    [Fact]
    public void A_late_bound_entity_holding_a_mixed_collection_attribute_is_copied()
    {
        Entity email = new Entity("email");
        email["to"] = new EntityCollection(new List<Entity> { new Entity("activityparty"), new TestAccount() });

        Entity captured = UpdateAndCapture(email);

        Assert.NotSame(email, captured);
        Assert.Equal(typeof(Entity), captured.GetAttributeValue<EntityCollection>("to").Entities[1].GetType());
    }

    [Fact]
    public void A_late_bound_entity_with_plain_values_and_related_rows_is_passed_through()
    {
        Entity account = new Entity("account");
        account["name"] = "Zava";
        account["revenue"] = new Money(5m);
        account.RelatedEntities[new Relationship("account_contacts")] = new EntityCollection(new List<Entity> { new Entity("contact") });

        Assert.Same(account, UpdateAndCapture(account));
    }

    [Fact]
    public void A_late_bound_entity_with_an_early_bound_row_among_its_related_rows_is_copied()
    {
        Relationship relationship = new Relationship("account_parent_account");
        Entity account = new Entity("account");
        account.RelatedEntities[relationship] = new EntityCollection(new List<Entity> { new Entity("account"), new TestAccount { Name = "Child" } });

        Entity captured = UpdateAndCapture(account);

        Assert.NotSame(account, captured);
        Entity child = captured.RelatedEntities[relationship].Entities[1];
        Assert.Equal(typeof(Entity), child.GetType());
        Assert.Equal("Child", child.GetAttributeValue<string>("name"));
    }

    [Fact]
    public void A_request_with_an_early_bound_parameter_among_plain_ones_is_sanitized()
    {
        OrganizationRequest request = new OrganizationRequest("vela_Custom") { RequestId = Guid.NewGuid() };
        request["Note"] = "plain";
        request["Target"] = new TestAccount { Name = "Zava" };

        OrganizationRequest captured = ExecuteAndCapture(request);

        Assert.NotSame(request, captured);
        Assert.Equal(typeof(Entity), captured["Target"].GetType());
        Assert.Equal("plain", captured["Note"]);
        Assert.Equal("vela_Custom", captured.RequestName);
        Assert.Equal(request.RequestId, captured.RequestId);
        Assert.IsType<TestAccount>(request["Target"]);
    }

    [Fact]
    public void A_request_type_without_a_parameterless_constructor_falls_back_to_the_base_request()
    {
        NamedOnlyRequest request = new NamedOnlyRequest("vela_Named");
        request["Target"] = new TestAccount { Name = "Zava" };

        OrganizationRequest captured = ExecuteAndCapture(request);

        Assert.Equal(typeof(OrganizationRequest), captured.GetType());
        Assert.Equal("vela_Named", captured.RequestName);
        Assert.Equal(typeof(Entity), captured["Target"].GetType());
    }
}
