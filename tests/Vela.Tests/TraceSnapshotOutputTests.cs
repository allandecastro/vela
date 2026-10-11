using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// The exact text TraceMethod and TraceExecutionSnapshot write to the plug-in trace log: every header line, the
// metadata-only form (names, ids and counts, never values) and the full form (every value kind, nested entities,
// collections and parameter bags), in both the single-record and the bulk shape. The first line carries a timestamp,
// so it is checked on its own.
public sealed class TraceSnapshotOutputTests
{
    private static readonly string Nl = Environment.NewLine;
    private static readonly Guid AccountId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InitiatingId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Correlation = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ContactId = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Portal = new("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Tenant = new("77777777-7777-7777-7777-777777777777");

    private static PluginContextBuilder Builder()
    {
        PluginContextBuilder builder = new();
        builder.OnMessage("Update");
        FakePluginExecutionContext c = builder.Context;
        c.Stage = 20;
        c.Mode = 0;
        c.Depth = 2;
        c.PrimaryEntityName = "account";
        c.PrimaryEntityId = AccountId;
        c.UserId = UserId;
        c.InitiatingUserId = InitiatingId;
        c.CorrelationId = Correlation;
        return builder;
    }

    private static string Snapshot(LocalPluginContext ctx, RecordingTracingService traces, TraceSnapshotDetail detail)
    {
        traces.Clear();
        ctx.TraceExecutionSnapshot("PostOperation", detail);
        string output = Assert.Single(traces.Traces);
        string[] lines = output.Split(new[] { Nl }, StringSplitOptions.None);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T[^\]]+\] ExecutionSnapshot$", lines[0]);
        return string.Join(Nl, lines.Skip(1));
    }

    private static string Header(string extra = "") => string.Join(Nl,
        "  Phase: PostOperation",
        "  MessageName: Update",
        "  Stage: 20",
        "  Mode: 0",
        "  Depth: 2",
        $"  CorrelationId: {Correlation}",
        "  PrimaryEntityName: account",
        $"  PrimaryEntityId: {AccountId}",
        $"  UserId: {UserId}",
        $"  InitiatingUserId: {InitiatingId}") + Nl + extra;

    [Fact]
    public void An_empty_execution_snapshots_every_header_and_none_markers()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        string expected = Header() + string.Join(Nl,
            "  IsApplicationUser: False",
            "  IsPortalsClientCall: False",
            "  PortalsContactId: <none>",
            "  InitiatingUserAgent: <none>",
            "  EnvironmentId: <none>",
            "  TenantId: <none>",
            "  IsBulkOperation: False",
            "  InputParameters:",
            "    <none>",
            "  OutputParameters:",
            "    <none>",
            "  PreEntityImages:",
            "    <none>",
            "  PostEntityImages:",
            "    <none>",
            "  Target:",
            "    <none>");

        Assert.Equal(expected, Snapshot(ctx, builder.TraceCapture, TraceSnapshotDetail.Full));
    }

    [Fact]
    public void The_portal_agent_environment_and_tenant_are_traced_when_present()
    {
        PluginContextBuilder builder = Builder();
        builder.AsApplicationUser().WithEnvironmentId("env-1").WithTenantId(Tenant);
        builder.Context.IsPortalsClientCall = true;
        builder.Context.PortalsContactId = Portal;
        builder.Context.InitiatingUserAgent = "Browser/1.0";
        LocalPluginContext ctx = builder.Build();

        string snapshot = Snapshot(ctx, builder.TraceCapture, TraceSnapshotDetail.Metadata);

        Assert.Contains(string.Join(Nl,
            "  IsApplicationUser: True",
            "  IsPortalsClientCall: True",
            $"  PortalsContactId: {Portal}",
            "  InitiatingUserAgent: Browser/1.0",
            "  EnvironmentId: env-1",
            $"  TenantId: {Tenant}",
            "  IsBulkOperation: False"), snapshot, StringComparison.Ordinal);
    }

    private static Entity Account() => new("account", AccountId)
    {
        ["name"] = "Zava",
        ["creditlimit"] = new Money(1500m),
        ["industrycode"] = new OptionSetValue(7),
        ["primarycontactid"] = new EntityReference("contact", ContactId) { Name = "Ann" },
        ["donotemail"] = true,
        ["numberofemployees"] = 42,
        ["revenue"] = 2.5m,
        ["createdon"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        ["accountid"] = AccountId,
        ["description"] = null,
        ["contact.fullname"] = new AliasedValue("contact", "fullname", "Ann Lee"),
        ["parent"] = new Entity("account", ContactId),
        ["children"] = new EntityCollection(),
    };

    [Fact]
    public void The_full_snapshot_formats_every_value_kind()
    {
        PluginContextBuilder builder = Builder();
        builder.WithInputParameter("Target", Account());
        builder.WithInputParameter("Custom", "x");
        builder.WithOutputParameter("Rows", new EntityCollection { Entities = { new Entity("contact", ContactId) { ["lastname"] = "Lee" } } });
        builder.WithOutputParameter("Bag", new ParameterCollection { ["inner"] = 1L });
        builder.WithPreImage(new Entity("account", AccountId), "Before");
        LocalPluginContext ctx = builder.Build();

        string snapshot = Snapshot(ctx, builder.TraceCapture, TraceSnapshotDetail.Full);

        // The target account, as FormatEntity writes it at an indent of `pad` spaces.
        string AccountText(string pad) => string.Join(Nl, new[]
        {
            "LogicalName: account",
            $"Id: {AccountId}",
            "Attributes:",
            $"  accountid: {AccountId}",
            "  children:",
            "    EntityCollection(Count=0)",
            "  contact.fullname: AliasedValue[fullname]=\"Ann Lee\"",
            "  createdon: 2026-01-02T03:04:05.0000000Z",
            "  creditlimit: 1500 (Money)",
            "  description: <null>",
            "  donotemail: true",
            "  industrycode: 7 (OptionSetValue)",
            "  name: \"Zava\"",
            "  numberofemployees: 42",
            "  parent:",
            "    LogicalName: account",
            $"    Id: {ContactId}",
            "    Attributes: <none>",
            $"  primarycontactid: contact:{ContactId} Name=\"Ann\"",
            "  revenue: 2.5",
        }.Select(line => pad + line));

        string expected = Header() + string.Join(Nl,
            "  IsApplicationUser: False",
            "  IsPortalsClientCall: False",
            "  PortalsContactId: <none>",
            "  InitiatingUserAgent: <none>",
            "  EnvironmentId: <none>",
            "  TenantId: <none>",
            "  IsBulkOperation: False",
            "  InputParameters:",
            "    Custom:",
            "      \"x\"",
            "    Target:",
            AccountText("      "),
            "  OutputParameters:",
            "    Bag:",
            "      inner:",
            "        1",
            "    Rows:",
            "      EntityCollection(Count=1):",
            "        [0]:",
            "          LogicalName: contact",
            $"          Id: {ContactId}",
            "          Attributes:",
            "            lastname: \"Lee\"",
            "  PreEntityImages:",
            "    Before:",
            "      LogicalName: account",
            $"      Id: {AccountId}",
            "      Attributes: <none>",
            "  PostEntityImages:",
            "    <none>",
            "  Target:",
            AccountText("    "));

        Assert.Equal(expected, snapshot);
    }

    [Fact]
    public void The_metadata_snapshot_lists_names_ids_and_counts_but_no_values()
    {
        PluginContextBuilder builder = Builder();
        builder.WithInputParameter("Target", Account());
        builder.WithInputParameter("Owner", new EntityReference("systemuser", UserId));
        builder.WithInputParameter("Rows", new EntityCollection { EntityName = "contact", Entities = { new Entity("contact") } });
        builder.WithInputParameter("Key", ContactId);
        builder.WithInputParameter("Secret", "do-not-trace");
        builder.WithInputParameter("Nothing", null!);
        builder.WithPreImage(new Entity("account", AccountId) { ["name"] = "Old" }, "Before");
        builder.WithPostImage(new Entity("account", AccountId) { ["revenue"] = 1m, ["name"] = "New" }, "After");
        LocalPluginContext ctx = builder.Build();

        string snapshot = Snapshot(ctx, builder.TraceCapture, TraceSnapshotDetail.Metadata);

        string attributes = "accountid, children, contact.fullname, createdon, creditlimit, description, donotemail, industrycode, name, numberofemployees, parent, primarycontactid, revenue";
        Assert.EndsWith(string.Join(Nl,
            "  IsBulkOperation: False",
            $"  InputParameters: Key={ContactId}; Nothing=<null>; Owner=systemuser:{UserId}; Rows=EntityCollection(Count=1, contact); Secret=(String); Target=account:{AccountId} [{attributes}]",
            "  OutputParameters: <none>",
            $"  PreEntityImages: Before=account:{AccountId} [name]",
            $"  PostEntityImages: After=account:{AccountId} [name, revenue]",
            $"  Target: account:{AccountId} [{attributes}]"), snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-trace", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Zava", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void A_metadata_snapshot_without_target_or_images_says_none()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        Assert.EndsWith(string.Join(Nl,
            "  InputParameters: <none>",
            "  OutputParameters: <none>",
            "  PreEntityImages: <none>",
            "  PostEntityImages: <none>",
            "  Target: <none>"), Snapshot(ctx, builder.TraceCapture, TraceSnapshotDetail.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bulk_snapshot_counts_its_targets_and_images()
    {
        PluginContextBuilder builder = Builder();
        builder.OnMessage("UpdateMultiple");
        builder.WithTargets(new Entity("account", AccountId), new Entity("account", ContactId));
        builder.WithBulkPreImages(new EntityImageCollection { ["PreImage"] = new Entity("account", AccountId) });
        LocalPluginContext ctx = builder.Build();

        string snapshot = Snapshot(ctx, builder.TraceCapture, TraceSnapshotDetail.Metadata);

        Assert.Contains(string.Join(Nl,
            "  IsBulkOperation: True",
            "  BulkTargetsCount: 2",
            "  BulkPreEntityImagesCount: 1",
            "  BulkPostEntityImagesCount: 0"), snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void An_out_of_range_detail_traces_metadata_only()
    {
        PluginContextBuilder builder = Builder();
        builder.WithInputParameter("Secret", "do-not-trace");
        LocalPluginContext ctx = builder.Build();

        string snapshot = Snapshot(ctx, builder.TraceCapture, (TraceSnapshotDetail)7);

        Assert.Contains("  InputParameters: Secret=(String)", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-trace", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_phase_is_traced_as_unknown()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        ctx.TraceExecutionSnapshot("  ", TraceSnapshotDetail.Metadata);

        Assert.Contains($"{Nl}  Phase: unknown{Nl}", Assert.Single(builder.TraceCapture.Traces), StringComparison.Ordinal);
    }

    [Fact]
    public void TraceMethod_writes_the_method_and_its_execution_coordinates()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        ctx.TraceMethod("checking credit");
        ctx.TraceMethod();

        string[] traces = builder.TraceCapture.Traces.ToArray();
        Assert.Equal(2, traces.Length);
        string[] lines = traces[0].Split(new[] { Nl }, StringSplitOptions.None);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T[^\]]+\] method trace$", lines[0]);
        Assert.Equal(new[]
        {
            "  method      : TraceMethod_writes_the_method_and_its_execution_coordinates",
            "  message     : Update",
            "  stage/mode  : 20/0",
            "  depth       : 2",
            $"  correlation : {Correlation}",
            "  details     : checking credit",
        }, lines.Skip(1).ToArray());
        Assert.EndsWith($"{Nl}  details     : ", traces[1], StringComparison.Ordinal);
    }
}
