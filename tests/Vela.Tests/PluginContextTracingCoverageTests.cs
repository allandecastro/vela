using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Covers the diagnostics partial of PluginContextExtensions: TraceMethod, TraceExecutionSnapshot, and the
// value/entity/collection formatting helpers. A single rich snapshot walks every attribute type so the
// inline-value switch is exercised end to end.
public sealed class PluginContextTracingCoverageTests
{
    [Fact]
    public void Tracing_helpers_reject_a_null_context()
    {
        ILocalPluginContext ctx = null!;
        Assert.Throws<ArgumentNullException>(() => ctx.TraceMethod());
        Assert.Throws<ArgumentNullException>(() => ctx.TraceExecutionSnapshot("p"));
    }

    [Fact]
    public void TraceMethod_emits_a_line_with_and_without_details()
    {
        RecordingTracingService recorder = new RecordingTracingService();
        LocalPluginContext ctx = new PluginContextBuilder().WithTracingService(recorder).Build();

        ctx.TraceMethod();              // details null -> empty
        ctx.TraceMethod("some detail"); // details provided

        Assert.Equal(2, recorder.Traces.Count);
        Assert.Contains("method trace", recorder.Traces[0]);
        Assert.Contains("some detail", recorder.Traces[1]);
    }

    [Fact]
    public void Snapshot_of_an_empty_context_uses_the_none_placeholders()
    {
        RecordingTracingService recorder = new RecordingTracingService();
        LocalPluginContext ctx = new PluginContextBuilder().WithTracingService(recorder).Build();

        ctx.TraceExecutionSnapshot(string.Empty); // blank phase -> "unknown"

        string output = Assert.Single(recorder.Traces);
        Assert.Contains("Phase: unknown", output);
        Assert.Contains("<none>", output);              // empty params/images/target
        Assert.Contains("IsBulkOperation: False", output);
    }

    [Fact]
    public void Snapshot_formats_every_attribute_value_type()
    {
        RecordingTracingService recorder = new RecordingTracingService();
        Guid tenant = Guid.NewGuid();
        LocalPluginContext ctx = new PluginContextBuilder().WithTenantId(tenant).WithTracingService(recorder).Build();

        Entity target = new Entity("account", Guid.NewGuid())
        {
            ["text"] = "hello",
            ["when"] = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ["flag"] = true,
            ["choice"] = new OptionSetValue(7),
            ["amount"] = new Money(12.5m),
            ["owner"] = new EntityReference("systemuser", Guid.NewGuid()) { Name = "Bob" },
            ["aliased"] = new AliasedValue("acc", "name", "Acme"),
            ["uid"] = Guid.NewGuid(),
            ["count"] = 42,
            ["nested"] = new Entity("child", Guid.NewGuid()) { ["k"] = "v" },
            ["coll"] = new EntityCollection(new List<Entity> { new Entity("party", Guid.NewGuid()) }),
            ["emptycoll"] = new EntityCollection(),
            ["bag"] = new ParameterCollection { ["inner"] = "x" },
            ["unknown"] = TimeSpan.FromMinutes(3), // default ToString branch
        };
        ctx.PluginExecutionContext.InputParameters["Target"] = target;
        ctx.PluginExecutionContext.InputParameters["nil"] = null!;     // null -> <null>
        ctx.PluginExecutionContext.OutputParameters["result"] = 1;

        Entity pre = new Entity("account", target.Id) { ["text"] = "old" };
        ctx.PluginExecutionContext.PreEntityImages["PreImage"] = pre;

        ctx.TraceExecutionSnapshot("PostOperation");

        string output = Assert.Single(recorder.Traces);
        Assert.Contains("(OptionSetValue)", output);
        Assert.Contains("(Money)", output);
        Assert.Contains("systemuser:", output);
        Assert.Contains("AliasedValue[name]", output);
        Assert.Contains("EntityCollection(Count=1)", output);
        Assert.Contains("EntityCollection(Count=0)", output);
        Assert.Contains("<null>", output);
        Assert.Contains(tenant.ToString(), output);     // TenantId value branch
        Assert.Contains("LogicalName: child", output);  // nested entity formatting
    }

    [Fact]
    public void Snapshot_reports_bulk_operation_counts()
    {
        RecordingTracingService recorder = new RecordingTracingService();
        EntityImageCollection preImages = new EntityImageCollection();
        preImages["PreImage"] = new Entity("account", Guid.NewGuid());

        LocalPluginContext ctx = new PluginContextBuilder()
            .WithTracingService(recorder)
            .WithTargets(new EntityCollection(new List<Entity> { new Entity("account", Guid.NewGuid()) }))
            .WithBulkPreImages(preImages)
            .Build();

        ctx.TraceExecutionSnapshot("Bulk");

        string output = Assert.Single(recorder.Traces);
        Assert.Contains("IsBulkOperation: True", output);
        Assert.Contains("BulkTargetsCount: 1", output);
        Assert.Contains("BulkPreEntityImagesCount: 1", output);
    }
}
