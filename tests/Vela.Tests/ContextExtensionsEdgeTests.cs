using System;
using System.Collections.Generic;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Vela.Context;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Edge behaviour of the context extensions (plugins-context mutation area): environment-variable conversion and
// fallbacks, the key-based and relationship reads, HasChanged's preconditions, and the argument guards, each with
// the message or parameter name the caller sees.
public sealed class ContextExtensionsEdgeTests
{
    // ---- Environment variables --------------------------------------------------------------------------------------

    private const int TypeString = 100000000;
    private const int TypeSecret = 100000005;

    private static ILocalPluginContext Variables(params (string Name, string? Default, string? Value)[] variables)
    {
        List<Entity> rows = new();
        foreach ((string name, string? defaultValue, string? value) in variables)
        {
            Guid id = Guid.NewGuid();
            Entity definition = new("environmentvariabledefinition", id)
            {
                ["schemaname"] = name,
                ["type"] = new OptionSetValue(name.EndsWith("Secret", StringComparison.Ordinal) ? TypeSecret : TypeString),
            };
            if (defaultValue != null)
            {
                definition["defaultvalue"] = defaultValue;
            }

            rows.Add(definition);
            if (value != null)
            {
                rows.Add(new Entity("environmentvariablevalue", Guid.NewGuid())
                {
                    ["environmentvariabledefinitionid"] = new EntityReference("environmentvariabledefinition", id),
                    ["value"] = value,
                    ["statecode"] = new OptionSetValue(0),
                });
            }
        }

        return new PluginContextBuilder().WithSystemService(new InMemoryOrganizationService().Seed(rows.ToArray())).Build();
    }

    private enum Tier { Bronze, Gold }

    private sealed class Endpoint
    {
        public string? Url { get; set; }

        public int Retries { get; set; }
    }

    [Fact]
    public void A_variable_with_a_value_ignores_the_fallback()
    {
        ILocalPluginContext ctx = Variables(("adc_Url", null, "https://x"), ("adc_Count", null, " 5 "));

        Assert.Equal("https://x", ctx.GetEnvironmentVariable("adc_Url", "fallback"));
        Assert.Equal(5, ctx.GetEnvironmentVariableAs("adc_Count", 9));
        Assert.Equal(5, ctx.GetEnvironmentVariableAs<int>("adc_Count"));
    }

    [Fact]
    public void A_variable_without_a_value_uses_the_fallback_or_says_why_it_is_missing()
    {
        ILocalPluginContext ctx = Variables(("adc_Empty", null, null));

        Assert.Equal("fallback", ctx.GetEnvironmentVariable("adc_Empty", "fallback"));
        Assert.Equal(9, ctx.GetEnvironmentVariableAs("adc_Empty", 9));
        Assert.False(ctx.TryGetEnvironmentVariableAs("adc_Empty", out int none));
        Assert.Equal(0, none);
        Assert.Equal("Environment variable 'adc_Empty' has no value and no default value.",
            Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetEnvironmentVariableAs<int>("adc_Empty")).Message);
        Assert.Equal("Environment variable 'adc_Nope' is not defined in this environment.",
            Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetEnvironmentVariable("adc_Nope")).Message);
    }

    [Fact]
    public void Values_convert_to_every_supported_type()
    {
        Guid id = Guid.NewGuid();
        ILocalPluginContext ctx = Variables(
            ("adc_Yes", null, "yes"), ("adc_No", null, "0"), ("adc_True", null, " True "), ("adc_One", null, "1"), ("adc_NoWord", null, "NO"),
            ("adc_Id", null, " " + id + " "), ("adc_When", null, "2026-01-02T03:04:05Z"), ("adc_Tier", null, " gold "),
            ("adc_Price", null, "1.25"), ("adc_Json", null, "{\"Url\":\"https://e\",\"Retries\":3}"), ("adc_Text", null, " keep "));

        Assert.True(ctx.GetEnvironmentVariableAs<bool>("adc_Yes"));
        Assert.False(ctx.GetEnvironmentVariableAs<bool>("adc_No"));
        Assert.True(ctx.GetEnvironmentVariableAs<bool?>("adc_True"));
        Assert.True(ctx.GetEnvironmentVariableAs<bool>("adc_One"));
        Assert.False(ctx.GetEnvironmentVariableAs<bool>("adc_NoWord"));
        Assert.Equal(id, ctx.GetEnvironmentVariableAs<Guid>("adc_Id"));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), ctx.GetEnvironmentVariableAs<DateTime>("adc_When"));
        Assert.Equal(DateTimeKind.Utc, ctx.GetEnvironmentVariableAs<DateTime>("adc_When").Kind);
        Assert.Equal(Tier.Gold, ctx.GetEnvironmentVariableAs<Tier>("adc_Tier"));
        Assert.Equal(1.25m, ctx.GetEnvironmentVariableAs<decimal>("adc_Price"));
        Endpoint endpoint = ctx.GetEnvironmentVariableAs<Endpoint>("adc_Json");
        Assert.Equal(("https://e", 3), (endpoint.Url, endpoint.Retries));
        Assert.Equal(" keep ", ctx.GetEnvironmentVariableAs<string>("adc_Text"));
    }

    [Fact]
    public void A_value_that_cannot_convert_names_the_variable_and_the_type()
    {
        ILocalPluginContext ctx = Variables(("adc_Bad", null, "maybe"), ("adc_NotNumber", null, "abc"));

        InvalidPluginExecutionException badBool = Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetEnvironmentVariableAs<bool>("adc_Bad"));
        InvalidPluginExecutionException badInt = Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetEnvironmentVariableAs<int>("adc_NotNumber"));

        Assert.Equal("Environment variable 'adc_Bad' value could not be converted to Boolean: 'maybe' is not a valid boolean.", badBool.Message);
        Assert.IsType<FormatException>(badBool.InnerException);
        Assert.StartsWith("Environment variable 'adc_NotNumber' value could not be converted to Int32: ", badInt.Message, StringComparison.Ordinal);
        Assert.False(ctx.TryGetEnvironmentVariableAs("adc_NotNumber", out int value));
        Assert.Equal(0, value);
        Assert.True(ctx.TryGetEnvironmentVariableAs("adc_Bad", out string? text));
        Assert.Equal("maybe", text);
    }

    [Fact]
    public void A_secret_variable_is_refused_and_a_null_context_is_rejected()
    {
        ILocalPluginContext ctx = Variables(("adc_ApiSecret", null, "s3cret"));

        Assert.Equal("Environment variable 'adc_ApiSecret' is a Secret; reading secret values is not supported (they resolve through Azure Key Vault).",
            Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetEnvironmentVariable("adc_ApiSecret")).Message);
        Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => ((ILocalPluginContext)null!).GetEnvironmentVariable("x")).ParamName);
    }

    // ---- Key-based and relationship reads ------------------------------------------------------------------------

    private static (Mock<IOrganizationService> Service, List<RetrieveRequest> Requests) Retrieving()
    {
        List<RetrieveRequest> requests = new();
        Mock<IOrganizationService> service = new();
        service.Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Returns((OrganizationRequest r) =>
            {
                requests.Add((RetrieveRequest)r);
                return new RetrieveResponse { Results = { ["Entity"] = new Entity("account", Guid.NewGuid()) } };
            });
        return (service, requests);
    }

    [Fact]
    public void RetrieveByKey_asks_for_the_selected_columns_or_all_of_them()
    {
        (Mock<IOrganizationService> service, List<RetrieveRequest> requests) = Retrieving();
        EntityReference key = new("account", "accountnumber", "A-1");

        service.Object.RetrieveByKey<TestAccount>(key, a => a.Name);
        service.Object.RetrieveByKey<TestAccount>(key);

        Assert.Same(key, requests[0].Target);
        Assert.Equal(new[] { "name" }, requests[0].ColumnSet.Columns);
        Assert.False(requests[0].ColumnSet.AllColumns);
        Assert.True(requests[1].ColumnSet.AllColumns);
    }

    [Fact]
    public void RetrieveWithRelated_asks_for_the_selected_columns_or_all_of_them()
    {
        (Mock<IOrganizationService> service, List<RetrieveRequest> requests) = Retrieving();
        EntityReference target = new("account", Guid.NewGuid());

        service.Object.RetrieveWithRelated<TestAccount>(target, _ => { }, a => a.Name);
        service.Object.RetrieveWithRelated<TestAccount>(target, _ => { });

        Assert.Equal(new[] { "name" }, requests[0].ColumnSet.Columns);
        Assert.False(requests[0].ColumnSet.AllColumns);
        Assert.True(requests[1].ColumnSet.AllColumns);
    }

    [Fact]
    public void Include_rejects_a_column_and_an_expression_that_is_not_a_property()
    {
        IOrganizationService service = Retrieving().Service.Object;
        EntityReference target = new("account", Guid.NewGuid());

        ArgumentException column = Assert.Throws<ArgumentException>(
            () => service.RetrieveWithRelated<TestAccount>(target, i => i.Include(a => a.AccountId)));
        ArgumentException notProperty = Assert.Throws<ArgumentException>(
            () => service.RetrieveWithRelated<TestAccount>(target, i => i.Include(a => a.GetType())));

        Assert.StartsWith("'AccountId' is not a generated relationship navigation property (it needs [RelationshipSchemaName]).", column.Message, StringComparison.Ordinal);
        Assert.StartsWith("Include expects a navigation property, e.g. o => o.Order_Details.", notProperty.Message, StringComparison.Ordinal);
        Assert.Equal("navigation", Assert.Throws<ArgumentNullException>(
            () => service.RetrieveWithRelated<TestAccount>(target, i => i.Include(null!))).ParamName);
    }

    [Fact]
    public void Reads_and_writes_reject_missing_arguments_by_name()
    {
        IOrganizationService service = new Mock<IOrganizationService>().Object;
        EntityReference reference = new("account", Guid.NewGuid());
        TestAccount account = new();

        Assert.Equal("service", Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).RetrieveByKey<TestAccount>(reference)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentNullException>(() => service.RetrieveByKey<TestAccount>(null!)).ParamName);
        Assert.Equal("service", Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).UpsertByKey(account, reference)).ParamName);
        Assert.Equal("entity", Assert.Throws<ArgumentNullException>(() => service.UpsertByKey<TestAccount>(null!, reference)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentNullException>(() => service.UpsertByKey(account, null!)).ParamName);
        Assert.Equal("service", Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).RetrieveWithRelated<TestAccount>(reference, _ => { })).ParamName);
        Assert.Equal("target", Assert.Throws<ArgumentNullException>(() => service.RetrieveWithRelated<TestAccount>(null!, _ => { })).ParamName);
        Assert.Equal("includes", Assert.Throws<ArgumentNullException>(() => service.RetrieveWithRelated<TestAccount>(reference, null!)).ParamName);
        Assert.Equal("service", Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).CreateWithRelated(account)).ParamName);
        Assert.Equal("entity", Assert.Throws<ArgumentNullException>(() => service.CreateWithRelated(null!)).ParamName);
        Assert.Equal("service", Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).AssociateRelated(account)).ParamName);
        Assert.Equal("entity", Assert.Throws<ArgumentNullException>(() => service.AssociateRelated(null!)).ParamName);
    }

    // ---- HasChanged, responses, business errors -------------------------------------------------------------------

    [Fact]
    public void HasChanged_explains_a_missing_target_pre_image_or_attribute_list()
    {
        LocalPluginContext noTarget = new PluginContextBuilder().OnDelete().Build();
        LocalPluginContext noImage = PluginContext.For<TestAccount>().OnUpdate().WithTarget(new TestAccount { Name = "x" }).Build();

        Assert.StartsWith("HasChanged requires an Entity Target (Create/Update).",
            Assert.Throws<InvalidPluginExecutionException>(() => noTarget.HasChanged<TestAccount>(a => a.Name)).Message, StringComparison.Ordinal);
        Assert.StartsWith("HasChanged requires a registered pre-image (alias 'Before').",
            Assert.Throws<InvalidPluginExecutionException>(() => noImage.HasChanged<TestAccount>(a => a.Name, "Before")).Message, StringComparison.Ordinal);
        ArgumentException none = Assert.Throws<ArgumentException>(() => noImage.HasAnyChanged<TestAccount>());
        Assert.StartsWith("At least one attribute expression is required.", none.Message, StringComparison.Ordinal);
        Assert.Equal("attributes", Assert.Throws<ArgumentException>(() => noImage.HasAnyChanged<TestAccount>(null!)).ParamName);
        Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => ((ILocalPluginContext)null!).HasChanged<TestAccount>(a => a.Name)).ParamName);
    }

    [Fact]
    public void Typed_responses_share_the_results_bag()
    {
        PluginContextBuilder builder = new();
        builder.WithOutputParameter("id", Guid.Empty);
        LocalPluginContext ctx = builder.Build();
        OrganizationResponse raw = new() { Results = { ["x"] = 1 } };

        Assert.Same(ctx.PluginExecutionContext.OutputParameters, ctx.GetResponse<CreateResponse>().Results);
        Assert.Same(raw.Results, raw.ToResponse<WhoAmIResponse>().Results);
        Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => ((ILocalPluginContext)null!).GetResponse<CreateResponse>()).ParamName);
        Assert.Equal("response", Assert.Throws<ArgumentNullException>(() => ((OrganizationResponse)null!).ToResponse<WhoAmIResponse>()).ParamName);
    }

    // ---- Targets, images and merges --------------------------------------------------------------------------------

    [Fact]
    public void A_missing_target_is_described_by_its_expected_shape()
    {
        LocalPluginContext ctx = new PluginContextBuilder().OnDelete().Build();

        Assert.Equal("Input parameter 'Target' is not an EntityReference.", Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetTargetRef()).Message);
        Assert.Equal("Input parameter 'Target' is not an Entity.", Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetTarget<Entity>()).Message);
        Assert.Equal("Input parameter 'Targets' is not an EntityCollection.", Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetTargets()).Message);
        Assert.Equal("Neither image nor target is available for merge.", Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetPreImageMerged()).Message);
        Assert.Equal("Neither image nor target is available for merge at index 0.", Assert.Throws<InvalidPluginExecutionException>(() => ctx.GetPostImageMerged(0)).Message);
    }

    [Fact]
    public void A_bulk_index_out_of_range_has_no_image_and_a_negative_one_is_refused()
    {
        Guid id = Guid.NewGuid();
        PluginContextBuilder builder = new();
        builder.OnMessage("UpdateMultiple");
        builder.WithTargets(new Entity("account", id) { ["name"] = "new" });
        builder.WithBulkPreImages(new EntityImageCollection { ["PreImage"] = new Entity("account", id) { ["name"] = "old", ["city"] = "Lyon" } });
        LocalPluginContext ctx = builder.Build();

        Assert.Equal("old", ctx.GetPreImage(0)!["name"]);
        Assert.Null(ctx.GetPreImage(1));
        Assert.Null(ctx.GetPostImage(0));
        Assert.Equal("targetIndex", Assert.Throws<ArgumentOutOfRangeException>(() => ctx.GetPreImage(-1)).ParamName);
        Assert.StartsWith("Target index cannot be negative.", Assert.Throws<ArgumentOutOfRangeException>(() => ctx.GetPreImageMerged(-1)).Message, StringComparison.Ordinal);

        Entity merged = ctx.GetPreImageMerged(0);
        Assert.Equal(("new", "Lyon", id), ((string)merged["name"], (string)merged["city"], merged.Id));
        Entity targetOnly = ctx.GetPostImageMerged(0);
        Assert.Equal(("account", id, "new"), (targetOnly.LogicalName, targetOnly.Id, (string)targetOnly["name"]));
        Assert.False(targetOnly.Contains("city"));
    }

    [Fact]
    public void A_merge_past_the_last_bulk_target_returns_a_copy_of_the_image()
    {
        Guid id = Guid.NewGuid();
        Entity image = new("account", id) { ["name"] = "old" };
        PluginContextBuilder builder = new();
        builder.OnMessage("UpdateMultiple");
        builder.WithTargets(new Entity("account", Guid.NewGuid()));
        builder.WithBulkPreImages(new EntityImageCollection(), new EntityImageCollection { ["PreImage"] = image });
        LocalPluginContext ctx = builder.Build();

        Entity merged = ctx.GetPreImageMerged(1);
        merged["name"] = "changed";

        Assert.Equal(id, merged.Id);
        Assert.Equal("old", image["name"]);
    }

    [Fact]
    public void A_merge_copies_the_image_state_and_row_version()
    {
        Guid id = Guid.NewGuid();
        Entity image = new("account", id) { ["name"] = "old", RowVersion = "42", EntityState = EntityState.Changed };
        LocalPluginContext ctx = new PluginContextBuilder().OnUpdate().WithTarget(new Entity("account", id) { ["name"] = "new" }).WithPreImage(image).Build();

        Entity merged = ctx.GetPreImageMerged();

        Assert.Equal(("new", "42", EntityState.Changed), ((string)merged["name"], merged.RowVersion, merged.EntityState));
        Assert.Equal("old", image["name"]);
    }

    // ---- TracingOrganizationService: early-bound entities reach Dataverse as base entities -------------------------

    private static (TracingOrganizationService Service, Mock<IOrganizationService> Inner, RecordingTracingService Traces) Tracing()
    {
        PluginContextBuilder builder = new();
        Mock<IOrganizationService> inner = new();
        inner.Setup(s => s.Execute(It.IsAny<OrganizationRequest>())).Returns(new OrganizationResponse());
        return (new TracingOrganizationService(inner.Object, builder.Build(), "User"), inner, builder.TraceCapture);
    }

    [Fact]
    public void Early_bound_entities_in_a_request_are_downcast_and_the_callers_collection_is_left_alone()
    {
        (TracingOrganizationService service, Mock<IOrganizationService> inner, _) = Tracing();
        TestAccount early = new() { Name = "x" };
        Entity plain = new("account");
        EntityCollection targets = new() { EntityName = "account", MoreRecords = true, PagingCookie = "c", TotalRecordCount = 3 };
        targets.Entities.Add(new TestAccount { Name = "y" });
        targets.Entities.Add(plain);
        OrganizationRequest request = new("custom") { ["Target"] = early, ["Targets"] = targets, ["Count"] = 1 };

        service.Execute(request);

        Assert.Equal(typeof(Entity), request["Target"].GetType());
        Assert.Equal("x", ((Entity)request["Target"])["name"]);
        EntityCollection sent = (EntityCollection)request["Targets"];
        Assert.NotSame(targets, sent);
        Assert.Equal(("account", true, "c", 3), (sent.EntityName, sent.MoreRecords, sent.PagingCookie, sent.TotalRecordCount));
        Assert.Equal(typeof(Entity), sent.Entities[0].GetType());
        Assert.Same(plain, sent.Entities[1]);
        Assert.IsType<TestAccount>(targets.Entities[0]);
        Assert.Equal(1, request["Count"]);
        inner.Verify(s => s.Execute(request), Times.Once);
    }

    [Fact]
    public void Create_and_update_send_a_base_entity_and_keep_a_plain_one_as_is()
    {
        (TracingOrganizationService service, Mock<IOrganizationService> inner, _) = Tracing();
        Entity plain = new("account");

        service.Create(new TestAccount { Name = "x" });
        service.Update(plain);

        inner.Verify(s => s.Create(It.Is<Entity>(e => e.GetType() == typeof(Entity) && (string)e["name"] == "x")), Times.Once);
        inner.Verify(s => s.Update(plain), Times.Once);
    }

    [Fact]
    public void A_request_without_a_name_and_a_query_without_a_top_are_traced_as_such()
    {
        (TracingOrganizationService service, _, RecordingTracingService traces) = Tracing();

        service.Execute(new OrganizationRequest());
        service.RetrieveMultiple(new QueryExpression("account"));

        Assert.Contains(traces.Traces, t => t.StartsWith("Dataverse.Execute start scope=User", StringComparison.Ordinal) && t.EndsWith("request=UnknownRequest", StringComparison.Ordinal));
        Assert.Contains(traces.Traces, t => t.EndsWith("query=QueryExpression; entity=account; top=null", StringComparison.Ordinal));
    }

    [Fact]
    public void Message_helpers_reject_a_null_context()
    {
        ILocalPluginContext ctx = null!;

        Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => ctx.BusinessError("K")).ParamName);
        Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => ctx.GetMessage("K")).ParamName);
    }
}
