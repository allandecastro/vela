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

// End-to-end coverage for PackageScanner.BuildLocalStateFromPackages — the one path that carries
// `// Stryker disable all` because it Assembly.LoadFrom's a REAL compiled plugin assembly out of a .nupkg,
// instantiates each plugin type and invokes its definition method. The fixture (SamplePluginFixture) is a
// dependency-free netstandard2.0 DLL whose base type names match PackageScanner's name-prefix rule, so it loads
// cleanly in the net10 test host without any Vela.Plugins / Xrm resolution. This exercises the full orchestration
// the unit tests over the helpers can't: extract -> load -> reflect both plugin kinds -> normalize -> assemble.
public sealed class PackageScannerBuildStateTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly List<string> _temps = new();
    private readonly string _nupkg;

    public PackageScannerBuildStateTests()
    {
        _nupkg = BuildFixtureNupkg();
    }

    [Fact]
    public void Builds_local_state_from_a_real_compiled_plugin_package()
    {
        SharedOptions options = OptionsFor(_nupkg);

        RegistrationState state = PackageScanner.BuildLocalStateFromPackages(options, Web, _temps);

        // The fixture assembly is discovered and recorded as a source assembly and a package payload.
        Assert.Contains("SamplePluginFixture", state.SourceAssemblies);
        Assert.Contains(state.PackagePayloads, p => p.PackageName == "SamplePluginFixture");
        Assert.Contains(state.SourceAssemblyPayloads, a => a.AssemblyName == "SamplePluginFixture");

        // The step plugin was instantiated, its GetStepDefinitions() invoked, and the result normalized.
        RegistrationItem step = Assert.Single(state.Items.Where(i => i.Kind == "step"));
        Assert.Equal("SamplePluginFixture.SampleAccountPlugin", step.Type);
        Assert.Equal("Create", step.Definition.GetProperty("message").GetString());
        Assert.Equal("account", step.Definition.GetProperty("entityLogicalName").GetString());

        // The Custom API plugin was instantiated, its GetCustomApiDefinition() invoked, and the result normalized.
        RegistrationItem api = Assert.Single(state.Items.Where(i => i.Kind == "custom-api"));
        Assert.Equal("SamplePluginFixture.SampleCustomApi", api.Type);
        Assert.Equal("sample_DoThing", api.Definition.GetProperty("uniqueName").GetString());
    }

    private static SharedOptions OptionsFor(string nupkg) => new SharedOptions(
        PackagePaths: new[] { nupkg },
        PackageAssembly: null,
        ConnectionString: null,
        OAuthUrl: null,
        OAuthToken: null,
        SolutionUniqueName: null,
        Environment: null,
        OutPath: null,
        Execute: false,
        Interactive: false,
        Strict: false,
        Verbose: false);

    // Zips the compiled fixture DLL (copied next to the test binaries via the project reference) into a minimal
    // plugin .nupkg — a .nuspec plus lib/netstandard2.0/SamplePluginFixture.dll — that the scanner can read.
    private string BuildFixtureNupkg()
    {
        string fixtureDll = Path.Combine(AppContext.BaseDirectory, "SamplePluginFixture.dll");
        Assert.True(File.Exists(fixtureDll), $"fixture assembly not found next to the tests: {fixtureDll}");

        string nupkg = Path.Combine(Path.GetTempPath(), $"SamplePluginFixture.{Guid.NewGuid():N}.nupkg");
        using (FileStream fs = new(nupkg, FileMode.CreateNew))
        using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry nuspec = zip.CreateEntry("SamplePluginFixture.nuspec");
            using (Stream s = nuspec.Open())
            {
                byte[] xml = Encoding.UTF8.GetBytes(
                    "<?xml version=\"1.0\"?><package><metadata><id>SamplePluginFixture</id><version>1.0.0</version></metadata></package>");
                s.Write(xml, 0, xml.Length);
            }

            zip.CreateEntryFromFile(fixtureDll, "lib/netstandard2.0/SamplePluginFixture.dll");
        }

        return nupkg;
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_nupkg))
            {
                File.Delete(_nupkg);
            }
        }
        catch
        {
            // best-effort cleanup
        }

        foreach (string dir in _temps)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
