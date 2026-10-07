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

    // An org where the assembly and plugin type exist (assemblyExists) with no steps, messages resolvable.
    private static FakeOrgService Org(bool assemblyExists = true, bool messageResolves = true)
    {
        Guid assemblyId = Guid.NewGuid();
        return new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => assemblyExists ? FakeOrgService.Collection(new Entity("pluginassembly", assemblyId) { ["name"] = "Zava.Plugins" }) : new EntityCollection(),
                "plugintype" => FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = Plugin }),
                "sdkmessage" => messageResolves
                    ? FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid()))
                    : throw new InvalidOperationException("message lookup failed"),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id),
        };
    }

    private static PlanApplyHost Host(FakeOrgService org, RegistrationState local, bool inputRedirected = true, string? answer = null) => new()
    {
        ScanLocal = (_, _, _) => local,
        Connect = _ => new OrganizationConnection(org),
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
    public void Plan_fails_up_front_on_an_unprefixed_package_without_a_solution()
    {
        string package = WritePackage("Plugins");
        RegistrationState local = Local(packages: [new PluginPackagePayload("Plugins", "1.0.0", package)]);

        int exit = Run("plan", Options(), Host(Org(), local));

        Assert.Equal(CliExitCodes.ExitError, exit);
        Assert.Contains("no solution publisher prefix", _error.ToString());
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
    public void A_mid_run_failure_exits_7_with_an_apply_failed_document()
    {
        // The step's message cannot be resolved while the write is captured: the run fails after the publish phase.
        int exit = Run("apply", Options(autoApprove: true), Host(Org(messageResolves: false), Local([LocalStep()])));

        Assert.Equal(CliExitCodes.ExitPartialApply, exit);
        JsonElement json = StdoutJson();
        Assert.Equal("apply-failed", json.GetProperty("mode").GetString());
        Assert.Contains(json.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("message lookup failed"));
        Assert.Contains("apply failed mid-run", _error.ToString());
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
