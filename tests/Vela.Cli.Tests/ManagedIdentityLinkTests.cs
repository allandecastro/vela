using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>
/// <c>--managed-identity</c> on plan/apply: plan reports each published package's pluginpackage.managedidentityid against
/// the option (linked / link / change), apply writes the pending links after the package publish, and without the option
/// the link is neither read nor written. End to end over the contract fixture package and StatefulOrgService, so what
/// apply writes is what the next plan reads.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ManagedIdentityLinkTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Guid Dev = new("0b9a3c1e-1111-4a2b-9c3d-000000000001");
    private static readonly Guid Test = new("0b9a3c1e-2222-4a2b-9c3d-000000000002");

    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private string _nupkg = ContractFixturePackage.Build();
    private readonly string _out = Path.Combine(Path.GetTempPath(), $"vela-mi-{Guid.NewGuid():N}.json");
    private readonly StatefulOrgService _org = NewEnvironment();

    public ManagedIdentityLinkTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        File.Delete(_nupkg);
        File.Delete(_out);
    }

    // ---- end to end ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_package_created_by_the_apply_is_linked_and_the_next_plan_is_clean()
    {
        Assert.Equal(ExitChangesPending, Run("plan", Dev, detailedExitCode: true));
        JsonElement plan = Output();
        JsonElement link = Assert.Single(plan.GetProperty("managedIdentity").GetProperty("packages").EnumerateArray());
        Assert.Equal(Dev, plan.GetProperty("managedIdentity").GetProperty("id").GetGuid());
        Assert.Equal((ContractFixturePackage.PackageId, "link"), (link.GetProperty("package").GetString(), link.GetProperty("status").GetString()));
        Assert.False(link.TryGetProperty("currentManagedIdentityId", out _));
        JsonElement[] packageOps = Operations(plan).Where(o => o.GetProperty("kind").GetString() == "package").ToArray();
        Assert.Equal(new[] { "create", "link" }, packageOps.Select(o => o.GetProperty("action").GetString()).OrderBy(a => a, StringComparer.Ordinal).ToArray());

        Assert.Equal(ExitSuccess, Run("apply", Dev));
        JsonElement applied = Output();
        Assert.Equal("apply-execute", applied.GetProperty("mode").GetString());
        Assert.Equal(1, applied.GetProperty("executed").GetProperty("linkedToManagedIdentity").GetInt32());
        Assert.Equal("linked", Assert.Single(applied.GetProperty("managedIdentity").GetProperty("packages").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(Dev, LinkedId());

        Assert.Equal(ExitSuccess, Run("plan", Dev, detailedExitCode: true));
        JsonElement second = Output();
        Assert.False(second.GetProperty("hasChanges").GetBoolean());
        Assert.Empty(Operations(second));
        Assert.Equal("linked", Assert.Single(second.GetProperty("managedIdentity").GetProperty("packages").EnumerateArray()).GetProperty("status").GetString());

        int writes = _org.Writes.Count;
        Assert.Equal(ExitSuccess, Run("apply", Dev));
        Assert.Equal(writes, _org.Writes.Count);
        Assert.Equal(0, Output().GetProperty("executed").GetProperty("linkedToManagedIdentity").GetInt32());
    }

    [Fact]
    public void An_unlinked_deployed_package_is_a_pending_change_on_its_own()
    {
        Assert.Equal(ExitSuccess, Run("apply"));
        Assert.Null(LinkedId());

        Assert.Equal(ExitChangesPending, Run("plan", Dev, detailedExitCode: true));
        JsonElement plan = Output();
        Assert.True(plan.GetProperty("hasChanges").GetBoolean());
        JsonElement summary = plan.GetProperty("summary");
        Assert.Equal((0, 0, 0), (summary.GetProperty("added").GetInt32(), summary.GetProperty("changed").GetInt32(), summary.GetProperty("removed").GetInt32()));
        JsonElement op = Assert.Single(Operations(plan));
        Assert.Equal(("link", "package", ContractFixturePackage.PackageId), (op.GetProperty("action").GetString(), op.GetProperty("kind").GetString(), op.GetProperty("type").GetString()));
        Assert.Equal($"Plugin package is not linked to managed identity {Dev}; apply will link it.", op.GetProperty("reason").GetString());

        int writes = _org.Writes.Count;
        Assert.Equal(ExitSuccess, Run("apply", Dev));
        // Only the link: the package content is unchanged, so it is not republished.
        Assert.Equal(new[] { "update:pluginpackage" }, _org.Writes.Skip(writes).ToArray());
        Assert.False(Output().GetProperty("executed").GetProperty("packageRepublished").GetBoolean());
        Assert.Equal(Dev, LinkedId());
    }

    [Fact]
    public void A_package_linked_to_another_managed_identity_is_changed()
    {
        Assert.Equal(ExitSuccess, Run("apply", Dev));

        Assert.Equal(ExitChangesPending, Run("plan", Test, detailedExitCode: true));
        JsonElement plan = Output();
        JsonElement link = Assert.Single(plan.GetProperty("managedIdentity").GetProperty("packages").EnumerateArray());
        Assert.Equal("change", link.GetProperty("status").GetString());
        Assert.Equal(Dev, link.GetProperty("currentManagedIdentityId").GetGuid());
        Assert.Equal($"Plugin package is linked to managed identity {Dev}; apply will link it to {Test}.",
            Assert.Single(Operations(plan)).GetProperty("reason").GetString());

        Assert.Equal(ExitSuccess, Run("apply", Test));
        Assert.Equal(Test, LinkedId());
        Assert.Equal(ExitSuccess, Run("plan", Test, detailedExitCode: true));
    }

    [Fact]
    public void Without_the_option_the_link_is_neither_reported_nor_touched()
    {
        Assert.Equal(ExitSuccess, Run("apply", Dev));

        Assert.Equal(ExitSuccess, Run("plan", detailedExitCode: true));
        JsonElement plan = Output();
        Assert.False(plan.TryGetProperty("managedIdentity", out _));
        Assert.False(plan.GetProperty("hasChanges").GetBoolean());

        // A republish without the option keeps the link the environment has.
        File.Delete(_nupkg);
        _nupkg = ContractFixturePackage.Build(version: "1.0.1");
        Assert.Equal(ExitSuccess, Run("apply"));
        JsonElement applied = Output();
        Assert.True(applied.GetProperty("executed").GetProperty("packageRepublished").GetBoolean());
        Assert.Equal(0, applied.GetProperty("executed").GetProperty("linkedToManagedIdentity").GetInt32());
        Assert.False(applied.TryGetProperty("managedIdentity", out _));
        Assert.Equal(Dev, LinkedId());
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void An_unknown_managed_identity_fails_before_anything_is_written(string command)
    {
        int writes = _org.Writes.Count;
        Guid unknown = Guid.NewGuid();

        Assert.Equal(ExitError, Run(command, unknown));

        Assert.Contains($"Managed identity not found: {unknown}.", _stderr.ToString());
        Assert.False(File.Exists(_out));
        Assert.Equal(writes, _org.Writes.Count);
    }

    [Fact]
    public void Inspect_rejects_the_option()
    {
        Assert.Equal(ExitInvalidArguments, Run("inspect", Dev));
        Assert.Contains("--managed-identity applies to 'vela plan' and 'vela apply' only.", _stderr.ToString());
    }

    [Fact]
    public void A_link_the_environment_does_not_keep_is_reported_as_not_converged()
    {
        IOrganizationService dropsLinks = new DropsManagedIdentityLinks(_org);

        Assert.Equal(ExitDriftDetected, Run("apply", Dev, service: dropsLinks));

        JsonElement applied = Output();
        Assert.Equal("apply-not-converged", applied.GetProperty("mode").GetString());
        Assert.Equal("link", Assert.Single(applied.GetProperty("managedIdentity").GetProperty("packages").EnumerateArray()).GetProperty("status").GetString());
        Assert.Contains("/ 1 managed identity link(s) remain.", _stderr.ToString());
    }

    // ---- apply engine ---------------------------------------------------------------------------------------------

    [Fact]
    public void Apply_links_through_the_solution_scoped_service_after_the_publish()
    {
        Guid packageId = Guid.NewGuid();
        FakeOrgService org = new()
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginpackage"
                ? FakeOrgService.Collection(new Entity("pluginpackage", packageId) { ["name"] = "zava_Plugins" })
                : new EntityCollection(),
        };
        ManagedIdentityPlan plan = new(Dev,
        [
            new ManagedIdentityPackageLink("zava_Plugins", ManagedIdentityPackageLink.Link),
            new ManagedIdentityPackageLink("zava_Other", ManagedIdentityPackageLink.Linked),
        ]);

        DataverseRegistration.ApplyDiff(org, EmptyDiff(), [], "ZavaSales", verbose: false, managedIdentity: plan);

        UpdateRequest request = Assert.Single(org.Executed.OfType<UpdateRequest>());
        Assert.Equal("ZavaSales", request["SolutionUniqueName"]);
        Assert.Equal(("pluginpackage", packageId), (request.Target.LogicalName, request.Target.Id));
        EntityReference reference = request.Target.GetAttributeValue<EntityReference>("managedidentityid");
        Assert.Equal(("managedidentity", Dev), (reference.LogicalName, reference.Id));
        Assert.Single(request.Target.Attributes); // nothing but the link
        QueryExpression lookup = Assert.Single(org.Queries, q => q.EntityName == "pluginpackage");
        Assert.Equal("zava_Plugins", Assert.Single(lookup.Criteria.Conditions).Values.Single());
    }

    [Fact]
    public void Apply_without_a_solution_writes_the_link_directly()
    {
        FakeOrgService org = new()
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginpackage"
                ? FakeOrgService.Collection(new Entity("pluginpackage", Guid.NewGuid()) { ["name"] = "zava_Plugins" })
                : new EntityCollection(),
        };

        DataverseRegistration.ApplyDiff(org, EmptyDiff(), [], null, verbose: false,
            managedIdentity: new ManagedIdentityPlan(Dev, [new ManagedIdentityPackageLink("zava_Plugins", ManagedIdentityPackageLink.Change, Test)]));

        Assert.Empty(org.Executed.OfType<UpdateRequest>());
        Entity update = Assert.Single(org.Updated);
        Assert.Equal(Dev, update.GetAttributeValue<EntityReference>("managedidentityid").Id);
    }

    [Fact]
    public void A_package_missing_after_the_publish_fails_with_the_link_in_the_resume_report()
    {
        FakeOrgService org = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.ApplyDiff(org, EmptyDiff(), [], null, verbose: false,
            managedIdentity: new ManagedIdentityPlan(Dev, [new ManagedIdentityPackageLink("zava_Plugins", ManagedIdentityPackageLink.Link)])));

        Assert.Equal($"Plugin package 'zava_Plugins' was not found after the publish, so it cannot be linked to managed identity {Dev}.", ex.Message);
        Assert.Empty(org.Updated);
        string report = _stderr.ToString();
        Assert.Contains($"failed at: link plugin package zava_Plugins to managed identity {Dev}", report);
        Assert.Contains("+ publish plugin package(s) and resolve plugin types", report);
    }

    // ---- read side --------------------------------------------------------------------------------------------------

    [Fact]
    public void Link_states_follow_the_live_lookup_and_the_create_decision()
    {
        Guid samePackage = Guid.NewGuid();
        FakeOrgService org = new()
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "solution" => FakeOrgService.Collection(new Entity("solution", Guid.NewGuid()) { ["publisherid"] = new EntityReference("publisher", Guid.NewGuid()) }),
                "pluginpackage" => ((string)q.Criteria.Conditions[0].Values[0]) switch
                {
                    "zava_Same" => FakeOrgService.Collection(new Entity("pluginpackage", samePackage) { ["managedidentityid"] = new EntityReference("managedidentity", Dev) }),
                    "zava_Other" => FakeOrgService.Collection(new Entity("pluginpackage", Guid.NewGuid()) { ["managedidentityid"] = new EntityReference("managedidentity", Test) }),
                    "zava_Unlinked" => FakeOrgService.Collection(new Entity("pluginpackage", Guid.NewGuid())),
                    "zava_EmptyDeployed" => FakeOrgService.Collection(new Entity("pluginpackage", Guid.NewGuid())),
                    _ => new EntityCollection(),
                },
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id) { ["customizationprefix"] = "zava" },
        };
        PluginPackagePayload[] payloads =
        [
            new("Same", "1.0.0", "unused"),
            new("Other", "1.0.0", "unused"),
            new("Unlinked", "1.0.0", "unused"),
            new("New", "1.0.0", "unused"),
            new("EmptyNew", "1.0.0", "unused", HasRegistrations: false),
            new("EmptyDeployed", "1.0.0", "unused", HasRegistrations: false),
        ];

        ManagedIdentityPackageLink[] links = DataverseRegistration.ComputeManagedIdentityLinks(org, payloads, "ZavaSales", Dev);

        Assert.Equal(
            new[]
            {
                new ManagedIdentityPackageLink("zava_Same", "linked"),
                new ManagedIdentityPackageLink("zava_Other", "change", Test),
                new ManagedIdentityPackageLink("zava_Unlinked", "link"),
                new ManagedIdentityPackageLink("zava_New", "link"),
                new ManagedIdentityPackageLink("zava_EmptyDeployed", "link"),
            },
            links);
        Assert.Equal(new[] { false, true, true, true, true }, links.Select(l => l.NeedsWrite).ToArray());
        Assert.All(org.Queries.Where(q => q.EntityName == "pluginpackage"), q => Assert.Contains("managedidentityid", q.ColumnSet.Columns));
    }

    [Fact]
    public void No_package_means_no_link_and_no_read()
    {
        FakeOrgService org = new();

        Assert.Empty(DataverseRegistration.ComputeManagedIdentityLinks(org, [], "ZavaSales", Dev));
        Assert.Empty(org.Queries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_managed_identity_record_is_looked_up_by_id(bool exists)
    {
        FakeOrgService org = new()
        {
            OnRetrieveMultiple = q => exists ? FakeOrgService.Collection(new Entity("managedidentity", Dev)) : new EntityCollection(),
        };

        Exception? error = Record.Exception(() => DataverseRegistration.EnsureManagedIdentityExists(org, Dev));

        QueryExpression query = Assert.Single(org.Queries);
        Assert.Equal(("managedidentity", 1), (query.EntityName, query.TopCount));
        ConditionExpression condition = Assert.Single(query.Criteria.Conditions);
        Assert.Equal(("managedidentityid", ConditionOperator.Equal, (object)Dev), (condition.AttributeName, condition.Operator, condition.Values.Single()));
        if (exists)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Equal(
                $"Managed identity not found: {Dev}. --managed-identity takes the id (managedidentityid) of a managedidentity record in the target environment; create that record first.",
                Assert.IsType<InvalidOperationException>(error).Message);
        }
    }

    // ---- output and options -----------------------------------------------------------------------------------------

    [Fact]
    public void The_human_summary_lists_every_package_link()
    {
        PlanPreview preview = OutputWriter.BuildPlanPreview("plan", "env", EmptyDiff(), [], [], managedIdentity: new ManagedIdentityPlan(Dev,
        [
            new ManagedIdentityPackageLink("zava_A", ManagedIdentityPackageLink.Linked),
            new ManagedIdentityPackageLink("zava_B", ManagedIdentityPackageLink.Link),
            new ManagedIdentityPackageLink("zava_C", ManagedIdentityPackageLink.Change, Test),
        ]));
        StringWriter writer = new();

        OutputWriter.WritePlanSummary(preview, writer, color: false);

        string text = writer.ToString();
        Assert.Contains($"managed identity {Dev}:", text);
        Assert.Contains("  = package: zava_A (already linked)", text);
        Assert.Contains("  + package: zava_B (will link)", text);
        Assert.Contains($"  ~ package: zava_C (will change from {Test})", text);
        Assert.Contains("  - link package: zava_B", text);
        Assert.Contains("  - link package: zava_C", text);
        Assert.DoesNotContain("link package: zava_A", text);
        Assert.True(preview.HasChanges);
    }

    [Fact]
    public void Already_linked_packages_are_not_a_change()
    {
        PlanPreview preview = OutputWriter.BuildPlanPreview("plan", "env", EmptyDiff(), [], [],
            managedIdentity: new ManagedIdentityPlan(Dev, [new ManagedIdentityPackageLink("zava_A", ManagedIdentityPackageLink.Linked)]));
        StringWriter writer = new();

        OutputWriter.WritePlanSummary(preview, writer, color: false);

        Assert.False(preview.HasChanges);
        Assert.Empty(preview.Operations);
        Assert.Contains("  = package: zava_A (already linked)", writer.ToString());
    }

    [Fact]
    public void The_human_summary_says_when_there_is_no_package_to_link()
    {
        StringWriter writer = new();
        OutputWriter.WriteManagedIdentity(new ManagedIdentityPlan(Dev, []), writer, color: false);
        Assert.Equal($"managed identity {Dev}:\n  (no plugin package to link)\n", writer.ToString().Replace("\r\n", "\n"));

        StringWriter none = new();
        OutputWriter.WriteManagedIdentity(null, none, color: false);
        Assert.Equal(string.Empty, none.ToString());
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("0b9a3c1e-1111-4a2b-9c3d-000000000001", true, "0b9a3c1e-1111-4a2b-9c3d-000000000001")]
    [InlineData("not-a-guid", false, null)]
    [InlineData("00000000-0000-0000-0000-000000000000", false, null)]
    public void The_option_takes_a_non_empty_guid(string? value, bool valid, string? expected)
    {
        string[] args = value == null ? ["--package", "x.nupkg"] : ["--package", "x.nupkg", "--managed-identity", value];

        bool ok = CommandLineOptions.TryReadManagedIdentityId(args, out Guid? id, out int exitCode);

        Assert.Equal(valid, ok);
        Assert.Equal(expected == null ? null : Guid.Parse(expected), id);
        Assert.Equal(valid ? ExitSuccess : ExitInvalidArguments, exitCode);
        if (!valid)
        {
            Assert.Contains("--managed-identity must be the id (a GUID) of a managedidentity record.", _stderr.ToString());
        }
    }

    [Theory]
    [InlineData("plan", true)]
    [InlineData("apply", true)]
    [InlineData("inspect", false)]
    public void Plan_and_apply_accept_the_option_and_inspect_does_not(string command, bool accepted)
    {
        Assert.Equal(accepted, CliCommands.ValueOptions(command).Contains("--managed-identity"));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static StatefulOrgService NewEnvironment()
    {
        StatefulOrgService org = new();
        org.SeedMessage("Create", "account", "contact");
        org.SeedMessage("Update", "account", "contact");
        org.SeedMessage("Delete", "account", "contact");
        org.SeedMessage("Associate");
        org.DefinePackage(
            ContractFixturePackage.PackageId,
            ContractFixturePackage.AssemblyName,
            "ContractPluginFixture.AccountContractPlugin",
            "ContractPluginFixture.ContactContractPlugin",
            "ContractPluginFixture.ScoreAccountApi",
            "ContractPluginFixture.ExportContactsApi",
            "ContractPluginFixture.PingApi");
        // The managedidentity records of this environment (created by the admin, outside Vela).
        org.Create(new Entity("managedidentity", Dev) { ["name"] = "dev" });
        org.Create(new Entity("managedidentity", Test) { ["name"] = "test" });
        org.Writes.Clear();
        return org;
    }

    private int Run(string command, Guid? managedIdentity = null, bool detailedExitCode = false, IOrganizationService? service = null)
    {
        SharedOptions options = ContractFixturePackage.Options([_nupkg], _out, detailedExitCode) with
        {
            SecureConfigs = ["ContractErpKey=s3cret"],
            ManagedIdentityId = managedIdentity,
        };
        PlanApplyHost host = new()
        {
            ScanLocal = PackageScanner.BuildLocalStateFromPackages,
            Connect = _ => new OrganizationConnection(service ?? _org),
            IsInputRedirected = () => true,
            IsOutputRedirected = () => true,
            ReadLine = () => throw new InvalidOperationException("apply must not prompt with --auto-approve"),
        };
        File.Delete(_out);
        return PlanApplyCommand.Run(command, options, Web, host);
    }

    private JsonElement Output() => JsonDocument.Parse(File.ReadAllText(_out)).RootElement.Clone();

    private static JsonElement[] Operations(JsonElement output) => output.GetProperty("operations").EnumerateArray().ToArray();

    private Guid? LinkedId() =>
        Assert.Single(_org.Rows("pluginpackage")).GetAttributeValue<EntityReference>("managedidentityid")?.Id;

    private static RegistrationDiff EmptyDiff() => new([], [], [], [], [], [], []);

    // An environment that accepts the link update but does not keep it.
    private sealed class DropsManagedIdentityLinks(IOrganizationService inner) : IOrganizationService
    {
        public Guid Create(Entity entity) => inner.Create(entity);

        public void Update(Entity entity)
        {
            if (entity.LogicalName == "pluginpackage")
            {
                entity.Attributes.Remove("managedidentityid");
                if (entity.Attributes.Count == 0)
                {
                    return;
                }
            }

            inner.Update(entity);
        }

        public void Delete(string entityName, Guid id) => inner.Delete(entityName, id);

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => inner.Retrieve(entityName, id, columnSet);

        public EntityCollection RetrieveMultiple(QueryBase query) => inner.RetrieveMultiple(query);

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            if (request is UpdateRequest update)
            {
                Update(update.Target);
                return new UpdateResponse();
            }

            return inner.Execute(request);
        }

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            inner.Associate(entityName, entityId, relationship, relatedEntities);

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            inner.Disassociate(entityName, entityId, relationship, relatedEntities);
    }
}
