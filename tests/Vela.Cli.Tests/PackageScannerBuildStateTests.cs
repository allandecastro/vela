using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// End-to-end coverage for PackageScanner.BuildLocalStateFromPackages — the orchestration that Assembly.LoadFrom's a
// REAL compiled plugin assembly out of a .nupkg, instantiates each plugin type and invokes its definition method.
// The fixtures (SamplePluginFixture / BrokenPluginFixture) are dependency-free netstandard2.0 DLLs whose base type
// names match PackageScanner's name-prefix rule, so they load cleanly in the net10 test host without any
// Vela.Plugins / Xrm resolution and with no extra NuGet dependency. Between them they exercise the happy path, the
// introspection-failure path, and the dedup/ordering of the assembled RegistrationState.
public sealed class PackageScannerBuildStateTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly List<string> _temps = new();
    private readonly List<string> _nupkgs = new();

    [Fact]
    public void Builds_local_state_from_a_real_compiled_plugin_package()
    {
        string nupkg = BuildNupkg("SamplePluginFixture.dll", "SamplePluginFixture");

        RegistrationState state = PackageScanner.BuildLocalStateFromPackages(OptionsFor(nupkg), Web, _temps);

        // The fixture assembly is discovered and recorded as a source assembly and a package payload.
        Assert.Equal(new[] { "SamplePluginFixture" }, state.SourceAssemblies);
        Assert.Contains(state.PackagePayloads, p => p.PackageName == "SamplePluginFixture");
        Assert.Contains(state.SourceAssemblyPayloads, a => a.AssemblyName == "SamplePluginFixture");

        // Both plugin kinds were instantiated, their definition methods invoked, and the results normalized —
        // ordered by kind then type, so "custom-api" precedes "step".
        Assert.Equal(new[] { "custom-api", "step" }, state.Items.Select(i => i.Kind).ToArray());

        RegistrationItem step = Assert.Single(state.Items.Where(i => i.Kind == "step"));
        Assert.Equal("SamplePluginFixture.SampleAccountPlugin", step.Type);
        Assert.Equal("Create", step.Definition.GetProperty("message").GetString());
        Assert.Equal("account", step.Definition.GetProperty("entityLogicalName").GetString());

        RegistrationItem api = Assert.Single(state.Items.Where(i => i.Kind == "custom-api"));
        Assert.Equal("SamplePluginFixture.SampleCustomApi", api.Type);
        Assert.Equal("sample_DoThing", api.Definition.GetProperty("uniqueName").GetString());
    }

    [Fact]
    public void ModelsCommand_extracts_authored_custom_api_names_and_excludes_them_from_all_mode()
    {
        // Follow-up #1: pointing `vela models --custom-apis` (ALL) at a built package must not regenerate the Custom
        // APIs the developer authored in code. Scan the fixture (its SampleCustomApi has uniqueName "sample_DoThing")
        // and confirm the name is extracted, then removed from a simulated ALL-mode Custom API name list.
        string nupkg = BuildNupkg("SamplePluginFixture.dll", "SamplePluginFixture");

        RegistrationState state = PackageScanner.BuildLocalStateFromPackages(OptionsFor(nupkg), Web, _temps);
        HashSet<string> authored = CustomApiReader.AuthoredCustomApiUniqueNames(state);

        Assert.Contains("sample_DoThing", authored);

        // Case-insensitive match against the environment's uniquename, and other Custom APIs are kept.
        string[] allInEnvironment = { "sample_DoThing", "zava_KeepMe", "other_KeepMeToo" };
        IReadOnlyList<string> kept = CustomApiReader.ExcludeAuthoredCustomApis(allInEnvironment, authored, out int excluded);

        Assert.Equal(1, excluded);
        Assert.Equal(new[] { "zava_KeepMe", "other_KeepMeToo" }, kept.ToArray());
    }

    [Fact]
    public void ExcludeAuthoredCustomApis_is_case_insensitive_and_reports_zero_when_nothing_authored()
    {
        string[] all = { "zava_Alpha", "zava_Beta" };

        // No authored names -> nothing excluded (the no-package default: ALL = every Custom API).
        IReadOnlyList<string> unfiltered = CustomApiReader.ExcludeAuthoredCustomApis(all, new HashSet<string>(), out int noneExcluded);
        Assert.Equal(0, noneExcluded);
        Assert.Equal(all, unfiltered.ToArray());

        // A differently-cased authored name still matches and is removed.
        HashSet<string> authored = new(StringComparer.OrdinalIgnoreCase) { "ZAVA_ALPHA" };
        IReadOnlyList<string> filtered = CustomApiReader.ExcludeAuthoredCustomApis(all, authored, out int oneExcluded);
        Assert.Equal(1, oneExcluded);
        Assert.Equal(new[] { "zava_Beta" }, filtered.ToArray());
    }

    [Fact]
    public void Scanning_leaves_the_extracted_temp_folders_deletable()
    {
        // The scanner byte-loads the package assemblies so the extracted .dll files are never locked. On Windows
        // Assembly.LoadFrom held them open for the process lifetime, so deleting the temp folder here failed with
        // "Access to the path '...dll' is denied". This asserts the folders can be removed right after the scan.
        string nupkg = BuildNupkg("SamplePluginFixture.dll", "SamplePluginFixture");
        List<string> temps = new();

        PackageScanner.BuildLocalStateFromPackages(OptionsFor(nupkg), Web, temps);

        Assert.NotEmpty(temps);
        foreach (string dir in temps)
        {
            Assert.True(Directory.Exists(dir));
            Directory.Delete(dir, recursive: true); // must not throw (no held file handle)
            Assert.False(Directory.Exists(dir));
        }
    }

    [Fact]
    public void A_plugin_that_cannot_be_introspected_fails_loudly()
    {
        string nupkg = BuildNupkg("BrokenPluginFixture.dll", "BrokenPluginFixture");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PackageScanner.BuildLocalStateFromPackages(OptionsFor(nupkg), Web, _temps));

        // BuildIntrospectionFailure names the type, what it was reading, and the underlying reason.
        Assert.Contains("BrokenPluginFixture.BrokenStepPlugin", ex.Message);
        Assert.Contains("steps", ex.Message);
        Assert.Contains("Missing (string, string) constructor.", ex.Message);
        Assert.Contains("refusing to continue", ex.Message);
        // Hints that a stale package is a common cause (the reported "old message/stage" confusion).
        Assert.Contains("the package may be out of date", ex.Message);
        Assert.Contains("dotnet build -c Release", ex.Message);
    }

    [Fact]
    public void Duplicate_packages_are_deduplicated_in_the_assembled_state()
    {
        // The same package passed twice: the state dedups source assemblies and package payloads.
        string nupkg = BuildNupkg("SamplePluginFixture.dll", "SamplePluginFixture");

        RegistrationState state = PackageScanner.BuildLocalStateFromPackages(OptionsFor(nupkg, nupkg), Web, _temps);

        Assert.Equal(new[] { "SamplePluginFixture" }, state.SourceAssemblies);
        Assert.Single(state.PackagePayloads);
        Assert.Single(state.SourceAssemblyPayloads);
    }

    private static SharedOptions OptionsFor(params string[] nupkgs) => new SharedOptions(
        PackagePaths: nupkgs,
        PackageAssembly: null,
        ConnectionString: null,
        OAuthUrl: null,
        OAuthToken: null,
        SolutionUniqueName: null,
        Environment: null,
        OutPath: null,
        AutoApprove: false,
        Interactive: false,
        Strict: false,
        Verbose: false);

    // Zips a compiled fixture DLL (copied next to the test binaries via its project reference) into a minimal
    // plugin .nupkg — a .nuspec plus lib/netstandard2.0/<dll> — that the scanner can read.
    private string BuildNupkg(string dllFileName, string packageId)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, dllFileName);
        Assert.True(File.Exists(dll), $"fixture assembly not found next to the tests: {dll}");

        string nupkg = Path.Combine(Path.GetTempPath(), $"{packageId}.{Guid.NewGuid():N}.nupkg");
        _nupkgs.Add(nupkg);
        using (FileStream fs = new(nupkg, FileMode.CreateNew))
        using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry nuspec = zip.CreateEntry($"{packageId}.nuspec");
            using (Stream s = nuspec.Open())
            {
                byte[] xml = Encoding.UTF8.GetBytes(
                    $"<?xml version=\"1.0\"?><package><metadata><id>{packageId}</id><version>1.0.0</version></metadata></package>");
                s.Write(xml, 0, xml.Length);
            }

            zip.CreateEntryFromFile(dll, "lib/netstandard2.0/" + dllFileName);
        }

        return nupkg;
    }

    public void Dispose()
    {
        foreach (string nupkg in _nupkgs)
        {
            TryDelete(() => File.Delete(nupkg), () => File.Exists(nupkg));
        }

        foreach (string dir in _temps)
        {
            TryDelete(() => Directory.Delete(dir, recursive: true), () => Directory.Exists(dir));
        }
    }

    private static void TryDelete(Action delete, Func<bool> exists)
    {
        try
        {
            if (exists())
            {
                delete();
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
