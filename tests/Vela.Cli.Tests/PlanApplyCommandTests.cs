using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xrm.Sdk;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// The plan / apply / inspect orchestration (PlanApplyCommand) end to end over a fake organization service: exit
/// codes, the JSON contract (schemaVersion, stdout purity under --verbose), --strict on a first deployment, the
/// confirmation gate, package-only changes and the partial-apply path.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class PlanApplyCommandTests : IDisposable
{
    private const string Plugin = "Zava.Plugins.AccountPlugin";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();
    private readonly List<string> _files = [];

    public PlanApplyCommandTests()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        foreach (string file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    private static SharedOptions Options(bool strict = false, bool verbose = false, bool json = true, bool autoApprove = false,
        string? outPath = null, bool detailedExitCode = false, string? solution = null) =>
        new(PackagePaths: [], PackageAssembly: null, ConnectionString: "unused", OAuthUrl: null, OAuthToken: null,
            SolutionUniqueName: solution, Environment: "https://zava.crm.dynamics.com", OutPath: outPath, AutoApprove: autoApprove,
            Interactive: false, Strict: strict, Verbose: verbose, Json: json, DetailedExitCode: detailedExitCode);

    private static RegistrationItem LocalStep(string key = "main", string message = "Update") =>
        new("step", Plugin, DefinitionNormalizer.NormalizeStepDefinition(
            JsonSerializer.SerializeToElement(new { pluginTypeName = Plugin, message, stage = 40, mode = 0, stepKey = key, filteringAttributes = new[] { "name" } }),
            JsonOptions));

    private static RegistrationState Local(RegistrationItem[]? items = null, PluginPackagePayload[]? packages = null, string[]? diagnostics = null) =>
        new(SourceAssemblies: ["Zava.Plugins"], SourceAssemblyPayloads: [], PackagePayloads: packages ?? [],
            GeneratedAtUtc: DateTime.UtcNow, Items: items ?? [], Diagnostics: diagnostics ?? []);

    // An org where the assembly and plugin type exist (assemblyExists) with no steps, messages resolvable. The steps
    // apply creates read back (keepsWrites), so an apply converges; without it, every write is silently dropped.
    private static FakeOrgService Org(bool assemblyExists = true, bool keepsWrites = true)
    {
        Guid assemblyId = Guid.NewGuid();
        Guid pluginTypeId = Guid.NewGuid();
        Dictionary<Guid, string> messageNames = [];
        FakeOrgService org = new() { OnRetrieve = (e, id, _) => new Entity(e, id) };
        org.OnRetrieveMultiple = q => q.EntityName switch
        {
            "pluginassembly" => assemblyExists ? FakeOrgService.Collection(new Entity("pluginassembly", assemblyId) { ["name"] = "Zava.Plugins" }) : new EntityCollection(),
            "plugintype" => FakeOrgService.Collection(new Entity("plugintype", pluginTypeId) { ["typename"] = Plugin }),
            "solution" => FakeOrgService.Collection(new Entity("solution", Guid.NewGuid()) { ["ismanaged"] = false }),
            "sdkmessage" => FakeOrgService.Collection(new Entity("sdkmessage", MessageId((string)q.Criteria.Conditions[0].Values[0]))),
            "sdkmessageprocessingstep" when keepsWrites => FakeOrgService.Collection(org.Created
                .Where(e => e.LogicalName == "sdkmessageprocessingstep")
                .Select(ReadBack)
                .ToArray()),
            _ => new EntityCollection(),
        };
        return org;

        Guid MessageId(string name)
        {
            Guid id = messageNames.FirstOrDefault(x => x.Value == name).Key;
            if (id == Guid.Empty)
            {
                id = Guid.NewGuid();
                messageNames[id] = name;
            }

            return id;
        }

        // A created step as the live read returns it: its columns plus the joined message name (these steps are global).
        Entity ReadBack(Entity created)
        {
            Entity row = new("sdkmessageprocessingstep", created.Id);
            foreach (KeyValuePair<string, object> attribute in created.Attributes)
            {
                row[attribute.Key] = attribute.Value;
            }

            row["msg.name"] = new AliasedValue("sdkmessage", "name", messageNames[created.GetAttributeValue<EntityReference>("sdkmessageid").Id]);
            return row;
        }
    }

    private static PlanApplyHost Host(FakeOrgService org, RegistrationState local, bool inputRedirected = true, string? answer = null, string? orgUrl = null) => new()
    {
        ScanLocal = (_, _, _) => local,
        Connect = _ => new OrganizationConnection(org, url: orgUrl),
        IsInputRedirected = () => inputRedirected,
        IsOutputRedirected = () => true,
        ReadLine = () => answer,
    };

    private int Run(string command, SharedOptions options, PlanApplyHost host) => PlanApplyCommand.Run(command, options, JsonOptions, host);

    private JsonElement StdoutJson() => JsonDocument.Parse(_out.ToString()).RootElement;

    // ---- plan --------------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_without_changes_exits_0_and_writes_a_versioned_json_document()
    {
        int exit = Run("plan", Options(), Host(Org(), Local()));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        JsonElement json = StdoutJson();
        Assert.Equal(MachineOutput.SchemaVersion, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("schemaVersion", json.EnumerateObject().First().Name); // first property, for cheap sniffing
        Assert.Equal("plan", json.GetProperty("mode").GetString());
        Assert.False(json.GetProperty("hasChanges").GetBoolean());
    }

    [Fact]
    public void Plan_with_changes_reports_them_and_exits_0_without_detailed_exitcode()
    {
        int exit = Run("plan", Options(), Host(Org(), Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        JsonElement json = StdoutJson();
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(1, json.GetProperty("summary").GetProperty("added").GetInt32());
        Assert.Contains(json.GetProperty("operations").EnumerateArray(), o => o.GetProperty("action").GetString() == "create" && o.GetProperty("kind").GetString() == "step");
    }

    [Theory]
    [InlineData(true, CliExitCodes.ExitChangesPending)]
    [InlineData(false, CliExitCodes.ExitSuccess)]
    public void Plan_detailed_exitcode_signals_pending_changes(bool withChange, int expected)
    {
        RegistrationState local = withChange ? Local([LocalStep()]) : Local();

        int exit = Run("plan", Options(detailedExitCode: true), Host(Org(), local));

        Assert.Equal(expected, exit);
        Assert.Equal(withChange, StdoutJson().GetProperty("hasChanges").GetBoolean());
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("inspect")]
    public void Detailed_exitcode_is_rejected_outside_plan(string command)
    {
        int exit = Run(command, Options(detailedExitCode: true), Host(Org(), Local()));

        Assert.Equal(CliExitCodes.ExitInvalidArguments, exit);
        Assert.Contains("--detailed-exitcode applies to 'vela plan' only", _error.ToString());
        Assert.Equal(string.Empty, _out.ToString());
    }

    [Fact]
    public void Plan_reports_a_code_only_package_publish_as_a_change()
    {
        // No step/Custom API change, but the package is not in Dataverse yet: apply would publish it, so plan must not
        // say "no changes" (and --detailed-exitcode must signal it).
        string package = WritePackage("zava_Plugins");
        RegistrationState local = Local(packages: [new PluginPackagePayload("zava_Plugins", "1.0.0", package)]);

        int exit = Run("plan", Options(detailedExitCode: true), Host(Org(), local));

        Assert.Equal(CliExitCodes.ExitChangesPending, exit);
        JsonElement json = StdoutJson();
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(0, json.GetProperty("summary").GetProperty("added").GetInt32());
        JsonElement op = Assert.Single(json.GetProperty("operations").EnumerateArray());
        Assert.Equal("package", op.GetProperty("kind").GetString());
        Assert.Equal("create", op.GetProperty("action").GetString());
    }

    [Fact]
    public void Plan_does_not_propose_creating_an_empty_package()
    {
        // A freshly scaffolded project: the package registers nothing and is not in Dataverse. Nothing to do, but say why.
        string package = WritePackage("zava_Plugins");
        RegistrationState local = Local(packages: [new PluginPackagePayload("zava_Plugins", "1.0.0", package, HasRegistrations: false)]);

        int exit = Run("plan", Options(detailedExitCode: true), Host(Org(), local));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        JsonElement json = StdoutJson();
        Assert.False(json.GetProperty("hasChanges").GetBoolean());
        Assert.Empty(json.GetProperty("operations").EnumerateArray());
        Assert.Contains(json.GetProperty("info").EnumerateArray(),
            i => i.GetString() == "Plugin package 'zava_Plugins' contains no plugin or Custom API; it is not created in Dataverse until it does.");
    }

    [Fact]
    public void Plan_still_republishes_an_empty_package_already_in_Dataverse()
    {
        string package = WritePackage("zava_Plugins");
        Guid packageId = Guid.NewGuid();
        FakeOrgService org = Org();
        org.PackageFiles.Store(packageId, [1, 2, 3]); // a different stored .nupkg
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner = org.OnRetrieveMultiple;
        org.OnRetrieveMultiple = q => q.EntityName == "pluginpackage"
            ? FakeOrgService.Collection(new Entity("pluginpackage", packageId) { ["name"] = "zava_Plugins", ["version"] = "1.0.0" })
            : inner(q);
        RegistrationState local = Local(packages: [new PluginPackagePayload("zava_Plugins", "1.0.0", package, HasRegistrations: false)]);

        int exit = Run("plan", Options(detailedExitCode: true), Host(org, local));

        Assert.Equal(CliExitCodes.ExitChangesPending, exit);
        JsonElement op = Assert.Single(StdoutJson().GetProperty("operations").EnumerateArray());
        Assert.Equal("package", op.GetProperty("kind").GetString());
        Assert.Equal("update", op.GetProperty("action").GetString());
    }

    [Fact]
    public void Plan_accepts_an_unprefixed_empty_package_without_a_solution()
    {
        // The package is not imported, so the prefix guard does not apply to it.
        string package = WritePackage("Plugins");
        RegistrationState local = Local(packages: [new PluginPackagePayload("Plugins", "1.0.0", package, HasRegistrations: false)]);

        int exit = Run("plan", Options(), Host(Org(), local));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.False(StdoutJson().GetProperty("hasChanges").GetBoolean());
    }

    [Fact]
    public void Plan_fails_up_front_on_an_unprefixed_package_without_a_solution()
    {
        string package = WritePackage("Plugins");
        RegistrationState local = Local(packages: [new PluginPackagePayload("Plugins", "1.0.0", package)]);

        int exit = Run("plan", Options(), Host(Org(), local));

        Assert.Equal(CliExitCodes.ExitError, exit);
        Assert.Contains("no solution publisher prefix", _error.ToString());
    }

    // A .ForRelationship(...) Associate step: the scope is runtime-only (Dataverse registers Associate for every
    // relationship), so plan shows it but never diffs it.
    private static RegistrationItem ScopedAssociateStep(params string[] relationships) =>
        new("step", Plugin, DefinitionNormalizer.NormalizeStepDefinition(
            JsonSerializer.SerializeToElement(new { pluginTypeName = Plugin, message = "Associate", stage = 40, mode = 0, stepKey = "link-contacts", relationships }),
            JsonOptions));

    [Fact]
    public void Plan_shows_the_relationship_scope_of_an_associate_step_in_json_and_human_output()
    {
        int exit = Run("plan", Options(verbose: true), Host(Org(), Local([ScopedAssociateStep("account_contacts", "contact_customer_accounts")])));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        JsonElement added = Assert.Single(StdoutJson().GetProperty("diff").GetProperty("added").EnumerateArray());
        Assert.Equal(
            ["account_contacts", "contact_customer_accounts"],
            added.GetProperty("definition").GetProperty("relationships").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.Contains($"+ step: {Plugin} - PostOpSync: link-contacts [filter=none; pre=0; post=0; relationships=account_contacts,contact_customer_accounts]", _error.ToString());
    }

    [Fact]
    public void A_step_without_a_relationship_scope_carries_no_relationships_field()
    {
        Run("plan", Options(verbose: true), Host(Org(), Local([LocalStep()])));

        JsonElement added = Assert.Single(StdoutJson().GetProperty("diff").GetProperty("added").EnumerateArray());
        Assert.False(added.GetProperty("definition").TryGetProperty("relationships", out _));
        Assert.DoesNotContain("relationships=", _error.ToString());
    }

    [Fact]
    public void A_relationship_scoped_step_converges_and_replans_with_no_change()
    {
        // The live read has no relationship scope to return: comparing it would leave the step "changed" forever.
        FakeOrgService org = Org();
        RegistrationState local = Local([ScopedAssociateStep("account_contacts")]);

        int applyExit = Run("apply", Options(autoApprove: true), Host(org, local));
        Assert.Equal(CliExitCodes.ExitSuccess, applyExit);
        Assert.Equal("apply-execute", StdoutJson().GetProperty("mode").GetString());
        _out.GetStringBuilder().Clear();

        int planExit = Run("plan", Options(detailedExitCode: true), Host(org, Local([ScopedAssociateStep("contact_customer_accounts")])));

        // Same step key, another scope: still no diff (the scope lives in the package code, not in the registration).
        Assert.Equal(CliExitCodes.ExitSuccess, planExit);
        Assert.False(StdoutJson().GetProperty("hasChanges").GetBoolean());
    }

    // ---- --strict ----------------------------------------------------------------------------------------------

    [Fact]
    public void Strict_does_not_fail_a_first_deployment()
    {
        // The assembly is not in Dataverse yet: that is information, not a warning, so --strict passes (A-43).
        int exit = Run("plan", Options(strict: true), Host(Org(assemblyExists: false), Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        JsonElement json = StdoutJson();
        Assert.Empty(json.GetProperty("diagnostics").EnumerateArray());
        Assert.Contains(json.GetProperty("info").EnumerateArray(), i => i.GetString()!.Contains("'Zava.Plugins' not found"));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void Strict_fails_on_a_warning_before_anything_is_written(string command)
    {
        FakeOrgService org = Org();

        int exit = Run(command, Options(strict: true, autoApprove: true), Host(org, Local([LocalStep()], diagnostics: ["rank collision"])));

        Assert.Equal(CliExitCodes.ExitError, exit);
        Assert.Contains("Strict mode failed with 1 diagnostic(s).", _error.ToString());
        Assert.Contains("rank collision", _error.ToString());
        Assert.Empty(org.Created);
        Assert.Empty(org.Batches);
        Assert.Equal(string.Empty, _out.ToString());
    }

    [Fact]
    public void Without_strict_a_warning_is_reported_but_does_not_fail()
    {
        int exit = Run("plan", Options(), Host(Org(), Local(diagnostics: ["rank collision"])));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Equal("rank collision", Assert.Single(StdoutJson().GetProperty("diagnostics").EnumerateArray()).GetString());
    }

    // ---- output contract ---------------------------------------------------------------------------------------

    [Fact]
    public void Verbose_keeps_the_json_on_stdout_and_puts_the_summary_on_stderr()
    {
        int exit = Run("plan", Options(verbose: true), Host(Org(), Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.True(StdoutJson().GetProperty("hasChanges").GetBoolean()); // stdout parses as JSON
        Assert.Contains("summary: +1 -0 ~0", _error.ToString());
    }

    [Fact]
    public void Out_writes_the_document_to_the_file_and_nothing_to_stdout()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-plan-{Guid.NewGuid():N}.json");
        _files.Add(path);

        int exit = Run("plan", Options(outPath: path), Host(Org(), Local()));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Equal(string.Empty, _out.ToString());
        Assert.Contains("Plan written:", _error.ToString());
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Inspect_writes_a_versioned_document_with_both_sides()
    {
        int exit = Run("inspect", Options(), Host(Org(assemblyExists: false), Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        JsonElement json = StdoutJson();
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("inspect", json.GetProperty("mode").GetString());
        Assert.Single(json.GetProperty("local").GetProperty("steps").EnumerateArray());
        Assert.Empty(json.GetProperty("current").GetProperty("steps").EnumerateArray());
        Assert.Contains(json.GetProperty("info").EnumerateArray(), i => i.GetString()!.Contains("not found"));
    }

    // ---- apply -------------------------------------------------------------------------------------------------

    [Fact]
    public void Apply_refuses_in_a_non_interactive_session_without_auto_approve()
    {
        FakeOrgService org = Org();

        int exit = Run("apply", Options(), Host(org, Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitError, exit);
        Assert.Contains("Refusing to apply without confirmation", _error.ToString());
        Assert.Empty(org.Created);
    }

    [Theory]
    [InlineData("no", false)]
    [InlineData("yes", true)]
    public void Apply_on_a_terminal_writes_only_after_a_typed_yes(string answer, bool writes)
    {
        FakeOrgService org = Org();

        int exit = Run("apply", Options(), Host(org, Local([LocalStep()]), inputRedirected: false, answer: answer));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Contains("Type 'yes' to continue", _error.ToString());
        Assert.Equal(writes, org.Created.Any(e => e.LogicalName == "sdkmessageprocessingstep"));
        Assert.Equal(!writes, _error.ToString().Contains("Apply cancelled."));
    }

    // The prompt names the organization the connection reached, not only the --environment label: a label (or a
    // connection string, which leaves no --url to show) cannot hide which environment is about to be written.
    [Fact]
    public void The_confirmation_prompt_names_the_connected_organization_url()
    {
        SharedOptions options = Options() with { Environment = "production" };

        Run("apply", options, Host(Org(), Local([LocalStep()]), inputRedirected: false, answer: "no", orgUrl: "https://zava-dev.crm.dynamics.com"));

        Assert.Contains("against production (https://zava-dev.crm.dynamics.com)?", _error.ToString());
    }

    [Theory]
    [InlineData("production", "https://zava.crm.dynamics.com", "production (https://zava.crm.dynamics.com)")]
    [InlineData("https://zava.crm.dynamics.com/", "https://zava.crm.dynamics.com", "https://zava.crm.dynamics.com")]
    [InlineData(null, "https://zava.crm.dynamics.com", "https://zava.crm.dynamics.com")]
    [InlineData("production", null, "production")]
    [InlineData(null, null, "Dataverse")]
    public void The_apply_target_combines_the_label_and_the_organization_url(string? label, string? url, string expected) =>
        Assert.Equal(expected, PlanApplyCommand.DescribeTarget(label, url));

    [Fact]
    public void Apply_reports_what_it_executed()
    {
        FakeOrgService org = Org();

        int exit = Run("apply", Options(autoApprove: true), Host(org, Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Single(org.Created, e => e.LogicalName == "sdkmessageprocessingstep");
        JsonElement json = StdoutJson();
        Assert.Equal("apply-execute", json.GetProperty("mode").GetString());
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        JsonElement executed = json.GetProperty("executed");
        Assert.Equal(1, executed.GetProperty("created").GetInt32());
        Assert.False(executed.GetProperty("packageRepublished").GetBoolean());
    }

    [Fact]
    public void Apply_that_does_not_converge_exits_with_drift_and_says_so()   // #445
    {
        // Every write succeeds, yet the re-read still shows the step missing (the platform dropped it): reporting
        // success would let CI go green on an environment the next apply would change again.
        FakeOrgService org = Org(keepsWrites: false);

        int exit = Run("apply", Options(autoApprove: true), Host(org, Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitDriftDetected, exit);
        Assert.Single(org.Created, e => e.LogicalName == "sdkmessageprocessingstep");
        JsonElement json = StdoutJson();
        Assert.Equal("apply-not-converged", json.GetProperty("mode").GetString());
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(1, json.GetProperty("summary").GetProperty("added").GetInt32());
        Assert.Equal(1, json.GetProperty("executed").GetProperty("created").GetInt32());
        Assert.Contains("Apply did not converge: 1 to create / 0 to update / 0 to delete remain.", _error.ToString());
    }

    [Fact]
    public void Apply_without_changes_executes_nothing()
    {
        FakeOrgService org = Org();

        int exit = Run("apply", Options(autoApprove: true), Host(org, Local()));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Empty(org.Created);
        Assert.Empty(org.Batches);
        JsonElement json = StdoutJson();
        Assert.Equal("apply-execute", json.GetProperty("mode").GetString());
        Assert.Equal(0, json.GetProperty("executed").GetProperty("created").GetInt32());
        Assert.Contains(json.GetProperty("notes").EnumerateArray(), n => n.GetString() == "No changes detected; nothing executed.");
    }

    [Fact]
    public void Apply_with_only_a_package_to_publish_publishes_it()
    {
        FakeOrgService org = Org();
        string package = WritePackage("zava_Plugins");
        RegistrationState local = Local(packages: [new PluginPackagePayload("zava_Plugins", "1.0.0", package)]);

        int exit = Run("apply", Options(autoApprove: true), Host(org, local));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Single(org.Created, e => e.LogicalName == "pluginpackage");
        Assert.True(StdoutJson().GetProperty("executed").GetProperty("packageRepublished").GetBoolean());
    }

    [Fact]
    public void Apply_does_not_create_an_empty_package()
    {
        FakeOrgService org = Org();
        string package = WritePackage("zava_Plugins");
        RegistrationState local = Local(packages: [new PluginPackagePayload("zava_Plugins", "1.0.0", package, HasRegistrations: false)]);

        int exit = Run("apply", Options(autoApprove: true), Host(org, local));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.DoesNotContain(org.Created, e => e.LogicalName == "pluginpackage");
        Assert.False(StdoutJson().GetProperty("executed").GetProperty("packageRepublished").GetBoolean());
    }

    [Fact]
    public void Apply_with_changes_elsewhere_still_skips_the_empty_package()
    {
        // Another change runs apply's write phase; the empty package must not be created by EnsurePluginPackages.
        FakeOrgService org = Org();
        string empty = WritePackage("zava_Empty");
        RegistrationState local = Local([LocalStep()], packages: [new PluginPackagePayload("zava_Empty", "1.0.0", empty, HasRegistrations: false)]);

        int exit = Run("apply", Options(autoApprove: true), Host(org, local));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.DoesNotContain(org.Created, e => e.LogicalName == "pluginpackage");
        Assert.Single(org.Created, e => e.LogicalName == "sdkmessageprocessingstep");
    }

    [Fact]
    public void A_mid_run_failure_exits_7_with_an_apply_failed_document()
    {
        // The preview resolves the step's message, then the lookup fails while the write is captured: the run fails
        // after the publish phase.
        FakeOrgService org = Org();
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner = org.OnRetrieveMultiple;
        int messageLookups = 0;
        org.OnRetrieveMultiple = q => q.EntityName == "sdkmessage" && ++messageLookups > 1
            ? throw new InvalidOperationException("message lookup failed")
            : inner(q);
        int exit = Run("apply", Options(autoApprove: true), Host(org, Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitPartialApply, exit);
        JsonElement json = StdoutJson();
        Assert.Equal("apply-failed", json.GetProperty("mode").GetString());
        Assert.Contains(json.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("message lookup failed"));
        Assert.Contains("apply failed mid-run", _error.ToString());
        // What was written and what was not, as in the resume report: the publish landed, the step create failed.
        JsonElement failure = json.GetProperty("failure");
        Assert.Contains("message lookup failed", failure.GetProperty("error").GetString());
        Assert.Equal(["publish plugin package(s) and resolve plugin types"], Strings(failure.GetProperty("completed")));
        Assert.Equal([$"create step {Plugin} (main)"], Strings(failure.GetProperty("failed")));
        Assert.Empty(Strings(failure.GetProperty("notApplied")));
    }

    [Fact]
    public void A_failure_after_the_registration_writes_still_reports_them_as_completed()
    {
        // Every registration write landed; adding the unchanged assembly to the solution fails.
        FakeOrgService org = Org();
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner = org.OnRetrieveMultiple;
        bool written = false;
        org.OnRetrieveMultiple = q =>
        {
            written |= org.Created.Exists(e => e.LogicalName == "sdkmessageprocessingstep");
            return q.EntityName == "solutioncomponent" && written ? throw new InvalidOperationException("solution read failed") : inner(q);
        };

        int exit = Run("apply", Options(autoApprove: true, solution: "ZavaSales"), Host(org, Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitPartialApply, exit);
        JsonElement failure = StdoutJson().GetProperty("failure");
        Assert.Contains($"create step {Plugin} (main)", Strings(failure.GetProperty("completed")));
        Assert.Equal(["add unchanged components to solution ZavaSales"], Strings(failure.GetProperty("failed")));
        Assert.Empty(Strings(failure.GetProperty("notApplied")));
    }

    [Fact]
    public void A_failure_of_the_read_back_after_apply_exits_7_with_an_apply_failed_document()
    {
        // Every write landed (the step and the solution add); reading the environment back to check convergence fails.
        // The run still ends with the apply-failed document and the partial-apply exit, not a generic error.
        FakeOrgService org = Org();
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner = org.OnRetrieveMultiple;
        org.OnRetrieveMultiple = q =>
            q.EntityName == "sdkmessageprocessingstep" && org.Executed.Any(r => r is Microsoft.Crm.Sdk.Messages.AddSolutionComponentRequest)
                ? throw new InvalidOperationException("read-back failed")
                : inner(q);

        int exit = Run("apply", Options(autoApprove: true, solution: "ZavaSales"), Host(org, Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitPartialApply, exit);
        JsonElement json = StdoutJson();
        Assert.Equal("apply-failed", json.GetProperty("mode").GetString());
        JsonElement failure = json.GetProperty("failure");
        Assert.Contains("read-back failed", failure.GetProperty("error").GetString());
        string[] completed = Strings(failure.GetProperty("completed"));
        Assert.Contains($"create step {Plugin} (main)", completed);
        Assert.Contains("add unchanged components to solution ZavaSales", completed);
        Assert.Equal([PlanApplyCommand.ReadBackOperation], Strings(failure.GetProperty("failed")));
        Assert.Empty(Strings(failure.GetProperty("notApplied")));
        Assert.Contains("read-back failed", _error.ToString());
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public void Plan_with_a_solution_lists_an_unchanged_component_missing_from_it()
    {
        int exit = Run("plan", Options(solution: "ZavaSales", detailedExitCode: true), Host(Org(), Local()));

        Assert.Equal(CliExitCodes.ExitChangesPending, exit);
        JsonElement json = StdoutJson();
        Assert.True(json.GetProperty("hasChanges").GetBoolean());
        JsonElement op = Assert.Single(json.GetProperty("operations").EnumerateArray());
        Assert.Equal("add-to-solution", op.GetProperty("action").GetString());
        Assert.Equal("assembly", op.GetProperty("kind").GetString());
        Assert.Equal("Zava.Plugins", op.GetProperty("type").GetString());
    }

    [Fact]
    public void Apply_with_a_solution_adds_the_unchanged_component_to_it()
    {
        FakeOrgService org = Org();

        int exit = Run("apply", Options(solution: "ZavaSales", autoApprove: true), Host(org, Local()));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        var request = Assert.IsType<Microsoft.Crm.Sdk.Messages.AddSolutionComponentRequest>(Assert.Single(org.Executed, r => r is Microsoft.Crm.Sdk.Messages.AddSolutionComponentRequest));
        Assert.Equal(DataverseRegistration.PluginAssemblyComponentType, request.ComponentType);
        Assert.Equal("ZavaSales", request.SolutionUniqueName);
        Assert.Equal(1, StdoutJson().GetProperty("executed").GetProperty("addedToSolution").GetInt32());
    }

    // A --solution that is managed in the target environment means the run points at a test or production
    // environment: plan and apply refuse it before anything is read or written.
    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void A_solution_managed_in_the_environment_is_refused_before_any_read_or_write(string command)
    {
        FakeOrgService org = Org();
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner = org.OnRetrieveMultiple;
        List<string> queried = [];
        org.OnRetrieveMultiple = q =>
        {
            queried.Add(q.EntityName);
            return q.EntityName == "solution"
                ? FakeOrgService.Collection(new Entity("solution", Guid.NewGuid()) { ["ismanaged"] = true })
                : inner(q);
        };

        int exit = Run(command, Options(solution: "ZavaSales", autoApprove: true), Host(org, Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitError, exit);
        Assert.Contains("Solution 'ZavaSales' is managed in https://zava.crm.dynamics.com", _error.ToString());
        Assert.Contains("development environment", _error.ToString());
        Assert.Equal(["solution"], queried);
        Assert.Empty(org.Created);
        Assert.Empty(org.Updated);
        Assert.Empty(org.Executed);
        Assert.Equal(string.Empty, _out.ToString());
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void A_solution_missing_from_the_environment_is_refused_before_any_write(string command)
    {
        FakeOrgService org = Org();
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner = org.OnRetrieveMultiple;
        org.OnRetrieveMultiple = q => q.EntityName == "solution" ? new EntityCollection() : inner(q);

        int exit = Run(command, Options(solution: "ZavaSales", autoApprove: true), Host(org, Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitError, exit);
        Assert.Contains("Solution not found: ZavaSales", _error.ToString());
        Assert.Empty(org.Created);
        Assert.Empty(org.Executed);
    }

    [Fact]
    public void Inspect_does_not_check_the_solution()
    {
        FakeOrgService org = Org();
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner = org.OnRetrieveMultiple;
        org.OnRetrieveMultiple = q => q.EntityName == "solution"
            ? FakeOrgService.Collection(new Entity("solution", Guid.NewGuid()) { ["ismanaged"] = true })
            : inner(q);

        int exit = Run("inspect", Options(solution: "ZavaSales"), Host(org, Local()));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
    }

    [Fact]
    public void Adding_an_unchanged_package_to_the_solution_is_not_a_package_republish()
    {
        // The package is deployed, identical, but outside the solution: the only operation is its add-to-solution.
        // That must neither flag a republish nor label the confirmation as a code change.
        string package = WritePackage("zava_Plugins");
        Guid packageId = Guid.NewGuid();
        FakeOrgService org = Org();
        org.PackageFiles.Store(packageId, File.ReadAllBytes(package)); // the stored .nupkg (content reads back empty)
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner = org.OnRetrieveMultiple;
        org.OnRetrieveMultiple = q => q.EntityName switch
        {
            "pluginpackage" => FakeOrgService.Collection(new Entity("pluginpackage", packageId) { ["name"] = "zava_Plugins", ["version"] = "1.0.0" }),
            "solution" => FakeOrgService.Collection(new Entity("solution", Guid.NewGuid()) { ["publisherid"] = new EntityReference("publisher", Guid.NewGuid()) }),
            "solutioncomponentdefinition" => FakeOrgService.Collection(new Entity("solutioncomponentdefinition", Guid.NewGuid()) { ["primaryentityname"] = "pluginpackage", ["solutioncomponenttype"] = 10_090 }),
            _ => inner(q),
        };
        org.OnRetrieve = (e, id, _) => new Entity(e, id) { ["customizationprefix"] = "zava" };
        RegistrationState local = Local(packages: [new PluginPackagePayload("zava_Plugins", "1.0.0", package)]);

        int exit = Run("apply", Options(solution: "ZavaSales"), Host(org, local, inputRedirected: false, answer: "yes"));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Contains("Apply 0 to create / 0 to update / 0 to delete against", _error.ToString());
        Assert.DoesNotContain("plugin package (code change)", _error.ToString());
        Assert.DoesNotContain(org.Created, e => e.LogicalName == "pluginpackage");
        Assert.Empty(org.Updated);
        JsonElement executed = StdoutJson().GetProperty("executed");
        Assert.False(executed.GetProperty("packageRepublished").GetBoolean());
        Assert.Contains(org.Executed.OfType<Microsoft.Crm.Sdk.Messages.AddSolutionComponentRequest>(), r => r.ComponentId == packageId);
    }

    // The publisher prefix of the --solution is read once per run. The preview (package actions, package assembly set,
    // solution membership), the publish and the read-back each needed it, and each used to query the solution and
    // retrieve its publisher again.
    [Fact]
    public void An_apply_with_a_solution_resolves_its_publisher_prefix_once()
    {
        string package = WritePackage("zava_Plugins");
        FakeOrgService org = Org();
        Func<Microsoft.Xrm.Sdk.Query.QueryExpression, EntityCollection> inner = org.OnRetrieveMultiple;
        org.OnRetrieveMultiple = q => q.EntityName switch
        {
            "solution" => FakeOrgService.Collection(new Entity("solution", Guid.NewGuid()) { ["publisherid"] = new EntityReference("publisher", Guid.NewGuid()) }),
            _ => inner(q),
        };
        int publisherReads = 0;
        org.OnRetrieve = (e, id, _) =>
        {
            publisherReads += e == "publisher" ? 1 : 0;
            return new Entity(e, id) { ["customizationprefix"] = "zava" };
        };
        RegistrationState local = Local([LocalStep()], packages: [new PluginPackagePayload("zava_Plugins", "1.0.0", package)]);

        int exit = Run("apply", Options(solution: "ZavaSales", autoApprove: true), Host(org, local));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Contains(org.Created, e => e.LogicalName == "pluginpackage");
        Assert.Equal(1, publisherReads);
    }

    [Fact]
    public void OnlyUnwritten_drops_the_components_apply_writes_or_deletes()
    {
        static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
        var diff = new RegistrationDiff([], [],
            Added: [],
            Removed:
            [
                new RegistrationItem("step", Plugin, J(new { name = "Removed step" })),
                new RegistrationItem("custom-api", "T", J(new { uniqueName = "zava_Removed" })),
            ],
            Changed:
            [
                new RegistrationChange("step", Plugin, J(new { name = "Changed step" }), J(new { name = "Changed step (new name)" })),
                new RegistrationChange("custom-api", "T", J(new { uniqueName = "zava_Changed" }), J(new { uniqueName = "zava_Changed" })),
            ],
            LocalItems: [], CurrentItems: []);
        SolutionMembershipAction A(string kind, string name, string? parent = null) => new(kind, name, 1, Guid.NewGuid(), parent);
        SolutionMembershipAction[] actions =
        [
            A("step", "Removed step"), A("step", "Changed step"), A("step", "Unchanged step"),
            A("custom-api", "zava_Removed"), A("custom-api", "zava_Changed"), A("custom-api", "zava_Same"),
            A("custom-api-parameter", "zava_Removed.In", "zava_Removed"), A("custom-api-parameter", "zava_Changed.In", "zava_Changed"),
            A("custom-api-response-property", "Out", null), A("package", "zava_Plugins"), A("assembly", "Zava.Plugins"),
        ];

        string[] kept = PlanApplyCommand.OnlyUnwritten(actions, diff, packageWritten: true).Select(a => a.Name).ToArray();
        string[] keptWithoutPublish = PlanApplyCommand.OnlyUnwritten(actions, diff, packageWritten: false).Select(a => a.Name).ToArray();

        Assert.Equal(["Unchanged step", "zava_Same", "zava_Changed.In", "Out", "Zava.Plugins"], kept);
        Assert.Contains("zava_Plugins", keptWithoutPublish);
        Assert.Empty(PlanApplyCommand.OnlyUnwritten([], diff, packageWritten: false));
    }

    // ---- failures ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_connection_failure_exits_6()
    {
        PlanApplyHost host = new()
        {
            ScanLocal = (_, _, _) => Local(),
            Connect = _ => throw new DataverseConnectionException("Dataverse connection failed: nope"),
            IsOutputRedirected = () => true,
        };

        Assert.Equal(CliExitCodes.ExitConnectionFailure, Run("plan", Options(), host));
        Assert.Contains("Dataverse connection failed: nope", _error.ToString());
        Assert.Equal(string.Empty, _out.ToString());
    }

    public static TheoryData<Exception, int> ReadFailures() => new()
    {
        { new System.Net.Http.HttpRequestException("No such host is known."), CliExitCodes.ExitConnectionFailure },
        { new InvalidOperationException("ServiceClient call failed", new System.Net.Sockets.SocketException(10054)), CliExitCodes.ExitConnectionFailure },
        { new TimeoutException("The request channel timed out."), CliExitCodes.ExitConnectionFailure },
        { new System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault>(new Microsoft.Xrm.Sdk.OrganizationServiceFault { Message = "Principal user is missing prvReadPluginAssembly" }), CliExitCodes.ExitError },
        { new InvalidOperationException("bad state"), CliExitCodes.ExitError },
    };

    // The connection opens lazily for a token-based client: a network error on the first read of the live state is
    // still a connection failure (6), while an error Dataverse answered with stays a command failure (4).
    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void A_failure_reading_the_live_state_maps_network_errors_to_exit_6(Exception failure, int expected)
    {
        PlanApplyHost host = new()
        {
            ScanLocal = (_, _, _) => Local(),
            Connect = _ => new OrganizationConnection(new ThrowingOrgService(failure)),
            IsOutputRedirected = () => true,
        };

        Assert.Equal(expected, Run("plan", Options(), host));
        Assert.Contains(failure.Message, _error.ToString());
    }

    private sealed class ThrowingOrgService(Exception failure) : Microsoft.Xrm.Sdk.IOrganizationService
    {
        public Guid Create(Microsoft.Xrm.Sdk.Entity entity) => throw failure;
        public Microsoft.Xrm.Sdk.Entity Retrieve(string entityName, Guid id, Microsoft.Xrm.Sdk.Query.ColumnSet columnSet) => throw failure;
        public void Update(Microsoft.Xrm.Sdk.Entity entity) => throw failure;
        public void Delete(string entityName, Guid id) => throw failure;
        public Microsoft.Xrm.Sdk.OrganizationResponse Execute(Microsoft.Xrm.Sdk.OrganizationRequest request) => throw failure;
        public void Associate(string entityName, Guid entityId, Microsoft.Xrm.Sdk.Relationship relationship, Microsoft.Xrm.Sdk.EntityReferenceCollection relatedEntities) => throw failure;
        public void Disassociate(string entityName, Guid entityId, Microsoft.Xrm.Sdk.Relationship relationship, Microsoft.Xrm.Sdk.EntityReferenceCollection relatedEntities) => throw failure;
        public Microsoft.Xrm.Sdk.EntityCollection RetrieveMultiple(Microsoft.Xrm.Sdk.Query.QueryBase query) => throw failure;
    }

    [Theory]
    [InlineData(true, CliExitCodes.ExitFileNotFound)]
    [InlineData(false, CliExitCodes.ExitError)]
    public void A_scan_failure_maps_to_its_exit_code_without_connecting(bool missingFile, int expected)
    {
        bool connected = false;
        PlanApplyHost host = new()
        {
            ScanLocal = (_, _, _) => missingFile ? throw new FileNotFoundException("gone.nupkg") : throw new InvalidOperationException("bad package"),
            Connect = _ =>
            {
                connected = true;
                return new OrganizationConnection(Org());
            },
            IsOutputRedirected = () => true,
        };

        Assert.Equal(expected, Run("plan", Options(), host));
        Assert.False(connected, "the scan runs before any connection is opened");
    }

    [Fact]
    public void The_connection_is_disposed_when_the_run_ends()
    {
        var lifetime = new Disposable();
        PlanApplyHost host = new()
        {
            ScanLocal = (_, _, _) => Local(),
            Connect = _ => new OrganizationConnection(Org(), lifetime),
            IsOutputRedirected = () => true,
        };

        Run("plan", Options(), host);

        Assert.True(lifetime.Disposed);
    }

    [Fact]
    public void Human_output_on_a_terminal_replaces_the_json()
    {
        PlanApplyHost host = new()
        {
            ScanLocal = (_, _, _) => Local([LocalStep()]),
            Connect = _ => new OrganizationConnection(Org()),
            IsOutputRedirected = () => false,
        };

        int exit = Run("plan", Options(json: false), host);

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Contains("summary: +1 -0 ~0", _out.ToString());
        Assert.DoesNotContain("schemaVersion", _out.ToString());
    }

    [Fact]
    public void ExtractCustomApiUniqueNames_reads_added_and_changed_apis_only()
    {
        JsonElement Api(string name) => JsonSerializer.SerializeToElement(new { uniqueName = name });
        var diff = new RegistrationDiff([], [], [new RegistrationItem("custom-api", "T", Api("zava_Add")), new RegistrationItem("step", "T", Api("zava_Step"))],
            [new RegistrationItem("custom-api", "T", Api("zava_Removed"))],
            [new RegistrationChange("custom-api", "T", Api("zava_Old"), Api("zava_New")), new RegistrationChange("custom-api", "T", Api(""), JsonSerializer.SerializeToElement(new { uniqueName = " " }))],
            [], []);

        Assert.Equal(["zava_Add", "zava_New"], PlanApplyCommand.ExtractCustomApiUniqueNames(diff).ToArray());
    }

    private sealed class Disposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private string WritePackage(string id)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{id}.{Guid.NewGuid():N}.nupkg");
        _files.Add(path);
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (StreamWriter writer = new(zip.CreateEntry($"{id}.nuspec").Open()))
        {
            writer.Write($"<?xml version=\"1.0\"?><package><metadata><id>{id}</id><version>1.0.0</version></metadata></package>");
        }

        return path;
    }
}
