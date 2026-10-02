using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// File/zip-driven tests for <see cref="PackageScanner"/>: package extraction (lib/*.dll selection, primary
/// pick, registrable filtering), the .nupkg payload/metadata readers, and the Custom-API definition error
/// branches — all exercised with crafted <c>.nupkg</c> fixtures rather than a built plugin package.
/// </summary>
public sealed class PackageScannerScanTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    public void Dispose()
    {
        foreach (string path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
        }
    }

    private static readonly byte[] DummyDll = Encoding.UTF8.GetBytes("not-a-real-assembly");

    private string BuildNupkg(params (string entry, byte[] content)[] entries)
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-scan-test-" + Guid.NewGuid().ToString("N") + ".nupkg");
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach ((string entry, byte[] content) in entries)
            {
                ZipArchiveEntry e = zip.CreateEntry(entry);
                using Stream s = e.Open();
                s.Write(content, 0, content.Length);
            }
        }

        _tempPaths.Add(path);
        return path;
    }

    private static byte[] Nuspec(string id, string? version = null)
    {
        string versionElement = version == null ? "" : $"\n    <version>{version}</version>";
        return Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\"?>\n<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\n  <metadata>\n    <id>{id}</id>{versionElement}\n  </metadata>\n</package>");
    }

    // ---- ExtractPackage --------------------------------------------------------------------------

    [Fact]
    public void ExtractPackage_extracts_lib_dlls_and_prefers_named_assembly()
    {
        string nupkg = BuildNupkg(
            ("lib/net462/Zava.Plugins.dll", DummyDll),
            ("lib/net462/System.Text.Json.dll", DummyDll),
            ("content/readme.txt", DummyDll),            // ignored: not lib/*.dll
            ("lib/net462/notes.xml", DummyDll));         // ignored: not a .dll

        var tempDirs = new List<string>();
        ExtractedPackage result = PackageScanner.ExtractPackage(nupkg, "Zava.Plugins", tempDirs);
        _tempPaths.AddRange(tempDirs);

        Assert.Equal("Zava.Plugins.dll", Path.GetFileName(result.PrimaryAssemblyPath));
        // Only the two lib DLLs are extracted; System.* is excluded from the registrable set.
        Assert.Equal(new[] { "Zava.Plugins.dll" }, result.RegistrableAssemblyPaths.Select(Path.GetFileName));
        Assert.Single(tempDirs);
        Assert.True(File.Exists(result.PrimaryAssemblyPath));
    }

    [Fact]
    public void ExtractPackage_picks_net_framework_when_multiple_target_frameworks_present()
    {
        // Same assembly shipped for two TFMs: only the net4x build should be extracted (the Dataverse sandbox
        // runs .NET Framework), and it must not collide with the netstandard build.
        string nupkg = BuildNupkg(
            ("lib/netstandard2.0/Zava.Plugins.dll", Encoding.UTF8.GetBytes("netstandard-build")),
            ("lib/net462/Zava.Plugins.dll", Encoding.UTF8.GetBytes("net462-build")));

        var tempDirs = new List<string>();
        ExtractedPackage result = PackageScanner.ExtractPackage(nupkg, "Zava.Plugins", tempDirs);
        _tempPaths.AddRange(tempDirs);

        Assert.Equal(new[] { "Zava.Plugins.dll" }, result.RegistrableAssemblyPaths.Select(Path.GetFileName)); // one, not two
        Assert.Equal("net462-build", File.ReadAllText(result.PrimaryAssemblyPath)); // the net4x build, not netstandard
    }

    [Theory]
    [InlineData(new[] { "net462" }, "net462")]
    [InlineData(new[] { "netstandard2.0", "net462" }, "net462")]      // .NET Framework preferred over netstandard
    [InlineData(new[] { "net462", "net471" }, "net471")]              // higher net4x version wins
    [InlineData(new[] { "net462", "net472" }, "net462")]              // ...but not above the net471 package cap
    [InlineData(new[] { "net471", "net48" }, "net471")]
    [InlineData(new[] { "net48", "netstandard2.0" }, "net48")]       // an over-cap net4x still beats netstandard
    [InlineData(new[] { "net8.0", "netstandard2.0" }, "netstandard2.0")] // netstandard over a non-loadable net8
    public void ChooseTargetFramework_prefers_the_sandbox_compatible_framework(string[] tfms, string expected)
    {
        Assert.Equal(expected, PackageScanner.ChooseTargetFramework(tfms));
    }

    [Theory]
    [InlineData("net462", false)]
    [InlineData("net47", false)]
    [InlineData("net471", false)]
    [InlineData("net472", true)]
    [InlineData("net48", true)]
    [InlineData("net481", true)]
    [InlineData("netstandard2.0", false)]
    [InlineData("net8.0", false)]
    [InlineData("", false)]
    public void ExceedsPluginPackageFrameworkCap_flags_frameworks_above_net471(string tfm, bool expected)
    {
        Assert.Equal(expected, PackageScanner.ExceedsPluginPackageFrameworkCap(tfm));
    }

    [Fact]
    public void ExtractPackage_uses_package_id_as_primary_when_no_preferred_name()
    {
        string nupkg = BuildNupkg(
            ("Zava.Plugins.nuspec", Nuspec("Zava.Plugins")),
            ("lib/net462/Zava.Plugins.dll", DummyDll),
            ("lib/net462/Some.Other.dll", DummyDll));

        var tempDirs = new List<string>();
        ExtractedPackage result = PackageScanner.ExtractPackage(nupkg, preferredAssemblyName: null, tempDirs);
        _tempPaths.AddRange(tempDirs);

        Assert.Equal("Zava.Plugins.dll", Path.GetFileName(result.PrimaryAssemblyPath));
    }

    [Fact]
    public void ExtractPackage_falls_back_to_first_non_dependency_assembly()
    {
        string nupkg = BuildNupkg(
            ("lib/net462/System.Text.Json.dll", DummyDll),
            ("lib/net462/Beta.Plugins.dll", DummyDll),
            ("lib/net462/Alpha.Plugins.dll", DummyDll));

        var tempDirs = new List<string>();
        ExtractedPackage result = PackageScanner.ExtractPackage(nupkg, preferredAssemblyName: null, tempDirs);
        _tempPaths.AddRange(tempDirs);

        // No preferred name / package id → first non-dependency assembly, ordinally ordered.
        Assert.Equal("Alpha.Plugins.dll", Path.GetFileName(result.PrimaryAssemblyPath));
    }

    [Fact]
    public void ExtractPackage_falls_back_to_first_when_all_are_dependencies()
    {
        string nupkg = BuildNupkg(
            ("lib/net462/System.Text.Json.dll", DummyDll),
            ("lib/net462/Microsoft.Bcl.dll", DummyDll));

        var tempDirs = new List<string>();
        ExtractedPackage result = PackageScanner.ExtractPackage(nupkg, preferredAssemblyName: null, tempDirs);
        _tempPaths.AddRange(tempDirs);

        // Every DLL looks like a dependency → fall back to the ordinally-first extracted DLL.
        Assert.Equal("Microsoft.Bcl.dll", Path.GetFileName(result.PrimaryAssemblyPath));
        Assert.Empty(result.RegistrableAssemblyPaths); // System.* and Microsoft.* are not registrable
    }

    [Fact]
    public void ExtractPackage_appends_dll_extension_to_preferred_name()
    {
        string nupkg = BuildNupkg(("lib/net462/My.Plugins.dll", DummyDll));
        var tempDirs = new List<string>();

        // Preferred name given without extension → matched as "My.Plugins.dll".
        ExtractedPackage result = PackageScanner.ExtractPackage(nupkg, "My.Plugins", tempDirs);
        _tempPaths.AddRange(tempDirs);

        Assert.Equal("My.Plugins.dll", Path.GetFileName(result.PrimaryAssemblyPath));
    }

    [Fact]
    public void ExtractPackage_matches_preferred_name_that_already_has_dll_extension()
    {
        string nupkg = BuildNupkg(("lib/net462/My.Plugins.dll", DummyDll), ("lib/net462/Other.Plugins.dll", DummyDll));
        var tempDirs = new List<string>();

        // Preferred name already ends in .dll → used as-is (no double extension).
        ExtractedPackage result = PackageScanner.ExtractPackage(nupkg, "My.Plugins.dll", tempDirs);
        _tempPaths.AddRange(tempDirs);

        Assert.Equal("My.Plugins.dll", Path.GetFileName(result.PrimaryAssemblyPath));
    }

    [Fact]
    public void ExtractPackage_returns_registrable_assemblies_in_ordinal_order()
    {
        string nupkg = BuildNupkg(
            ("lib/net462/Beta.Plugins.dll", DummyDll),
            ("lib/net462/Alpha.Plugins.dll", DummyDll),
            ("lib/net462/System.Text.Json.dll", DummyDll));

        var tempDirs = new List<string>();
        ExtractedPackage result = PackageScanner.ExtractPackage(nupkg, "Alpha.Plugins", tempDirs);
        _tempPaths.AddRange(tempDirs);

        Assert.Equal(new[] { "Alpha.Plugins.dll", "Beta.Plugins.dll" }, result.RegistrableAssemblyPaths.Select(Path.GetFileName));
    }

    [Fact]
    public void ExtractPackage_throws_when_no_assemblies_present()
    {
        string nupkg = BuildNupkg(("content/readme.txt", DummyDll), ("Zava.nuspec", Nuspec("Zava")));
        var tempDirs = new List<string>();

        var ex = Assert.Throws<InvalidOperationException>(() => PackageScanner.ExtractPackage(nupkg, null, tempDirs));
        _tempPaths.AddRange(tempDirs);
        Assert.Contains("No assemblies found", ex.Message);
    }

    // ---- ReadPluginPackagePayload ----------------------------------------------------------------

    [Fact]
    public void ReadPluginPackagePayload_uses_nuspec_id_and_version()
    {
        string nupkg = BuildNupkg(("Zava.Plugins.Package.nuspec", Nuspec("Zava.Plugins.Package", "2.3.4")));

        PluginPackagePayload payload = PackageScanner.ReadPluginPackagePayload(nupkg);

        Assert.Equal("Zava.Plugins.Package", payload.PackageName);
        Assert.Equal("2.3.4", payload.Version);
        Assert.Equal(nupkg, payload.PackagePath);
    }

    [Fact]
    public void ReadPluginPackagePayload_defaults_name_from_file_and_version_when_no_nuspec()
    {
        string nupkg = BuildNupkg(("lib/net462/X.dll", DummyDll));

        PluginPackagePayload payload = PackageScanner.ReadPluginPackagePayload(nupkg);

        Assert.Equal(Path.GetFileNameWithoutExtension(nupkg), payload.PackageName);
        Assert.Equal("1.0.0.0", payload.Version); // no <version> → default
    }

    [Fact]
    public void ReadPluginPackagePayload_defaults_version_when_nuspec_omits_it()
    {
        string nupkg = BuildNupkg(("Zava.nuspec", Nuspec("Zava.Plugins")));

        PluginPackagePayload payload = PackageScanner.ReadPluginPackagePayload(nupkg);

        Assert.Equal("Zava.Plugins", payload.PackageName);
        Assert.Equal("1.0.0.0", payload.Version);
    }

    // ---- ReadPackageId ---------------------------------------------------------------------------

    [Fact]
    public void ReadPackageId_reads_id_from_nuspec()
    {
        string nupkg = BuildNupkg(("Zava.nuspec", Nuspec("Zava.Plugins.Pkg", "1.0.0")));
        using ZipArchive archive = ZipFile.OpenRead(nupkg);

        Assert.Equal("Zava.Plugins.Pkg", PackageScanner.ReadPackageId(archive));
    }

    [Fact]
    public void ReadPackageId_returns_null_without_nuspec()
    {
        string nupkg = BuildNupkg(("lib/net462/X.dll", DummyDll));
        using ZipArchive archive = ZipFile.OpenRead(nupkg);

        Assert.Null(PackageScanner.ReadPackageId(archive));
    }

    // ---- TryGetDefinition error branches ---------------------------------------------------------

    public sealed class CustomApiMissingCtor : PackageScannerTests.CustomApiBase
    {
        public object GetCustomApiDefinition() => new { uniqueName = "x" };
    }

    public sealed class CustomApiNullDefinition : PackageScannerTests.CustomApiBase
    {
        public CustomApiNullDefinition(string a, string b) { _ = a; _ = b; }
        public object? GetCustomApiDefinition() => null;
    }

    public sealed class CustomApiThrows : PackageScannerTests.CustomApiBase
    {
        public CustomApiThrows(string a, string b) { _ = a; _ = b; }
        public object GetCustomApiDefinition() => throw new InvalidOperationException("api-boom");
    }

    [Fact]
    public void TryGetDefinition_fails_when_constructor_missing()
    {
        bool ok = PackageScanner.TryGetDefinition(typeof(CustomApiMissingCtor), "GetCustomApiDefinition", out object? def, out string? error);
        Assert.False(ok);
        Assert.Null(def);
        Assert.Contains("constructor", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetDefinition_fails_when_method_missing()
    {
        bool ok = PackageScanner.TryGetDefinition(typeof(PackageScannerTests.GoodCustomApi), "NoSuchMethod", out _, out string? error);
        Assert.False(ok);
        Assert.Contains("NoSuchMethod", error);
    }

    [Fact]
    public void TryGetDefinition_fails_when_definition_null()
    {
        bool ok = PackageScanner.TryGetDefinition(typeof(CustomApiNullDefinition), "GetCustomApiDefinition", out _, out string? error);
        Assert.False(ok);
        Assert.Contains("null", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetDefinition_captures_exception_from_method()
    {
        bool ok = PackageScanner.TryGetDefinition(typeof(CustomApiThrows), "GetCustomApiDefinition", out _, out string? error);
        Assert.False(ok);
        Assert.Contains("InvalidOperationException", error);
        Assert.Contains("api-boom", error);
    }

    // A plugin whose definition method returns a single (non-array) object → wrapped into a one-element set.
    public sealed class SingleDefinitionPlugin : PackageScannerTests.EntityPluginBase
    {
        public SingleDefinitionPlugin(string a, string b) { _ = a; _ = b; }
        public object GetStepDefinitions() => new { message = "Update" };
    }

    [Fact]
    public void TryGetDefinitions_wraps_single_non_array_result()
    {
        bool ok = PackageScanner.TryGetDefinitions(typeof(SingleDefinitionPlugin), "GetStepDefinitions", out object[]? defs, out string? error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(defs!);
    }

    // An abstract subclass of a matching base is still not an instantiable plugin type.
    public abstract class AbstractStepPlugin : PackageScannerTests.EntityPluginBase
    {
    }

    [Fact]
    public void IsPluginType_rejects_abstract_subclass_of_matching_base()
        => Assert.False(PackageScanner.IsPluginType(typeof(AbstractStepPlugin), "EntityPluginBase"));
}
