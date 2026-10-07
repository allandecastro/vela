using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

/// <summary>
/// A-76: what a batched apply reports must match what landed - the one-by-one fallback must not turn a write that
/// already landed into a failure, the resume report must list each owner under its own outcome (a continue-on-error
/// batch can fail an early owner and still apply a later one), and progress lines must not claim unexecuted writes.
/// </summary>
[Collection("ConsoleRedirect")]
public class BatchReportingTests
{
    private const string Account = "Zava.Plugins.AccountPlugin";
    private const string Contact = "Zava.Plugins.ContactPlugin";

    private static FakeOrgService Org(Func<Entity, Guid>? onCreate = null)
    {
        var svc = new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => FakeOrgService.Collection(
                    new Entity("plugintype", Guid.NewGuid()) { ["typename"] = Account },
                    new Entity("plugintype", Guid.NewGuid()) { ["typename"] = Contact }),
                "sdkmessage" => FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid())),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id),
        };
        if (onCreate != null)
        {
            svc.OnCreate = onCreate;
        }

        return svc;
    }

    private static RegistrationItem AddedStep(string plugin, string key) =>
        new("step", plugin, Json(new { pluginTypeName = plugin, message = "Update", stage = 40, mode = 0, stepKey = key }));

    private static RegistrationDiff Added(params RegistrationItem[] items) =>
        new(["Zava.Plugins"], ["Zava.Plugins"], items, [], [], [], []);

    private static string RunCapturingStderr(Action action)
    {
        TextWriter original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return captured.ToString();
    }

    [Fact]
    public void Fallback_replay_of_a_create_that_already_landed_is_not_a_failure()
    {
        // The batch runs on the server, then the call times out: the fallback replays every write, and the replayed
        // creates hit "duplicate key" because their client-assigned ids already exist. They landed - apply succeeds.
        FakeOrgService svc = Org();
        svc.ThrowAfterBatchRuns = true;

        string stderr = RunCapturingStderr(() =>
            DataverseRegistration.ApplyDiff(svc, Added(AddedStep(Account, "a"), AddedStep(Contact, "c")), [], null, [], verbose: false));

        Assert.Equal(2, svc.Created.Count(e => e.LogicalName == "sdkmessageprocessingstep"));
        Assert.DoesNotContain("FAILED", stderr);
        Assert.DoesNotContain("apply failed mid-run", stderr);
    }

    [Fact]
    public void A_duplicate_key_from_a_normal_batch_is_still_a_failure()
    {
        // Only a replayed create is forgiven: a duplicate inside a batch that ran normally is a real failure.
        Guid existing = Guid.NewGuid();
        FakeOrgService svc = Org(onCreate: e => e.LogicalName == "sdkmessageprocessingstep"
            ? throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = DataverseRegistration.DuplicateRecordErrorCode, Message = "dup" }, "dup")
            : existing);

        string stderr = RunCapturingStderr(() =>
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, Added(AddedStep(Account, "a")), [], null, [], verbose: false)));

        Assert.Contains($"failed at: create step {Account} (a)", stderr);
    }

    [Fact]
    public void Resume_report_lists_a_later_owner_that_landed_as_completed_and_every_failed_owner()
    {
        // Three steps in one continue-on-error batch: the 1st and 3rd fail, the 2nd lands. The report used to put
        // everything after the earliest failure under "not applied", including the step that was created.
        FakeOrgService svc = Org(onCreate: e =>
            e.LogicalName == "sdkmessageprocessingstep" && (e.GetAttributeValue<string>("name") ?? "").EndsWith(": bad", StringComparison.Ordinal)
                ? throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -1, Message = "rejected" }, "rejected")
                : e.Id);

        string stderr = RunCapturingStderr(() =>
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(
                svc, Added(AddedStep(Account, "bad"), AddedStep(Account, "good"), AddedStep(Contact, "bad")), [], null, [], verbose: false)));

        Assert.Contains("failed (2):", stderr);
        Assert.Contains($"! create step {Account} (bad)", stderr);
        Assert.Contains($"! create step {Contact} (bad)", stderr);
        Assert.Contains("completed (2):", stderr); // publish + the step that landed
        Assert.Contains($"+ create step {Account} (good)", stderr);
        Assert.Contains("not applied (0):", stderr);
    }

    [Fact]
    public void Verbose_progress_lines_are_printed_after_the_flush_with_their_real_outcome()
    {
        FakeOrgService svc = Org(onCreate: e =>
            e.LogicalName == "sdkmessageprocessingstep" && (e.GetAttributeValue<string>("name") ?? "").EndsWith(": bad", StringComparison.Ordinal)
                ? throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -1, Message = "rejected" }, "rejected")
                : e.Id);
        bool loggedBeforeFlush = false;
        svc.OnRetrieve = (e, id, _) => new Entity(e, id);

        string stderr = RunCapturingStderr(() =>
        {
            TextWriter err = Console.Error;
            svc.OnRetrieveMultiple = WrapToDetectEarlyLog(svc.OnRetrieveMultiple, err, () => loggedBeforeFlush = true);
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(
                svc, Added(AddedStep(Account, "good"), AddedStep(Contact, "bad")), [], null, [], verbose: true));
        });

        Assert.False(loggedBeforeFlush, "a '[step] create' line was written before the batch ran");
        string[] lines = stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Contains(lines, l => l.StartsWith("[step] create", StringComparison.Ordinal) && l.Contains(": good") && !l.EndsWith("(not applied)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("[step] create", StringComparison.Ordinal) && l.Contains(": bad") && l.EndsWith("(not applied)", StringComparison.Ordinal));
    }

    // Flags when a "[step] create" line reached stderr while the run was still reading (i.e. during capture).
    private static Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> WrapToDetectEarlyLog(
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner, TextWriter err, Action flag)
        => q =>
        {
            if (err.ToString()!.Contains("[step] create", StringComparison.Ordinal))
            {
                flag();
            }

            return inner(q);
        };

    [Fact]
    public void ApplyProgress_reports_the_cursor_as_failed_for_a_synchronous_throw()
    {
        var progress = new ApplyProgress(["delete step a", "publish", "create step b"]);
        progress.CompleteNext();

        string text = string.Join("\n", progress.BuildReport("boom"));

        Assert.Contains("failed at: publish", text);
        Assert.Contains("completed (1):", text);
        Assert.Contains("+ delete step a", text);
        Assert.Contains("not applied (1):", text);
        Assert.Contains("- create step b", text);
    }

    [Fact]
    public void ApplyProgress_lists_recorded_failures_and_completions_individually()
    {
        var progress = new ApplyProgress(["publish", "create step a", "create step b", "create step c"]);
        progress.CompleteNext();
        progress.Fail(1);
        progress.Complete(2);
        progress.MoveTo(4);

        string text = string.Join("\n", progress.BuildReport("boom"));

        Assert.Contains("failed at: create step a", text);
        Assert.Contains("completed (2):", text);
        Assert.Contains("+ create step b", text);
        Assert.Contains("not applied (1):", text);
        Assert.Contains("- create step c", text);
        Assert.True(progress.IsCompleted(2));
        Assert.False(progress.IsCompleted(1));
    }

    [Fact]
    public void ApplyProgress_never_lists_an_operation_both_completed_and_failed()
    {
        var progress = new ApplyProgress(["publish", "create step a"]);
        progress.Complete(1);
        progress.Fail(1);

        string text = string.Join("\n", progress.BuildReport("boom"));

        Assert.Contains("failed at: create step a", text);
        Assert.DoesNotContain("+ create step a", text);
        Assert.False(progress.IsCompleted(1));
    }

    [Fact]
    public void ApplyProgress_past_the_end_reports_an_unknown_operation()
    {
        var progress = new ApplyProgress(["publish"]);
        progress.CompleteNext();

        string text = string.Join("\n", progress.BuildReport("boom"));

        Assert.Contains("failed at: (unknown operation)", text);
        Assert.Contains("completed (1):", text);
    }

    [Fact]
    public void Report_with_several_failures_lists_each_one()
    {
        IReadOnlyList<string> lines = ApplyResumeReport.Build(["create step a", "create step b"], "boom", ["publish"], []);

        Assert.Contains("  failed (2):", lines);
        Assert.Contains("    ! create step a", lines);
        Assert.Contains("    ! create step b", lines);
        Assert.DoesNotContain(lines, l => l.Contains("failed at:"));
    }
}
