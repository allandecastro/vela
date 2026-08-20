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

// Targeted regression tests that pin down behaviours mutation testing found under-asserted in
// PackageScanner: the "definition method returned null" error branches, the bulk-image logical guard
// when the image property is not an array, and the whole of ExtractPackage (primary-assembly selection,
// lib/ filtering, and the "no assemblies" failure). Kept in their own file so the shape stubs and the
// on-disk .nupkg builder stay out of the broader PackageScannerTests.
public sealed class PackageScannerMutationTests
{
    // ---- definition method returns null (distinct from the empty-array branch already covered) ----

    public abstract class EntityPluginBaseStub
    {
    }

    public abstract class CustomApiBaseStub
    {
    }

    public sealed class NullStepsPlugin : EntityPluginBaseStub
    {
        public NullStepsPlugin(string a, string b)
        {
            _ = a;
            _ = b;
        }

        public object[]? GetStepDefinitions() => null;
    }

    public sealed class NullCustomApi : CustomApiBaseStub
    {
        public NullCustomApi(string a, string b)
        {
            _ = a;
            _ = b;
        }

        public object? GetCustomApiDefinition() => null;
    }

    [Fact]
    public void TryGetDefinitions_Fails_WhenMethodReturnsNull()
    {
        bool ok = PackageScanner.TryGetDefinitions(typeof(NullStepsPlugin), "GetStepDefinitions", out object[]? defs, out string? error);

        Assert.False(ok);
        Assert.Null(defs);
        Assert.Contains("GetStepDefinitions", error);
        Assert.Contains("returned null", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetDefinition_Fails_WhenMethodReturnsNull()
    {
        bool ok = PackageScanner.TryGetDefinition(typeof(NullCustomApi), "GetCustomApiDefinition", out object? def, out string? error);

        Assert.False(ok);
        Assert.Null(def);
        Assert.Contains("GetCustomApiDefinition", error);
        Assert.Contains("returned null", error, StringComparison.OrdinalIgnoreCase);
    }

    // ---- bulk-image guard: a non-array image property must not count as "has images" ----
    // Kills the &&-to-|| mutant on the HasItems predicate: with a present-but-not-an-array property the
    // ValueKind==Array clause is what makes the result false, so the two clauses cannot be OR-ed.

    [Fact]
    public void IsBulkStepWithImages_False_WhenImagePropertyIsNotAnArray()
    {
        JsonElement step = JsonDocument.Parse(
            "{\"message\":\"CreateMultiple\",\"preImages\":{\"alias\":\"pre\"},\"postImages\":42}").RootElement;

        Assert.False(PackageScanner.IsBulkStepWithImages(step));
    }

    // ---- ExtractPackage: primary-assembly selection and lib/ filtering ----

    [Fact]
    public void ExtractPackage_PrefersNamedAssembly_WhenPreferredNameLacksDllSuffix()
    {
        string nupkg = BuildNupkg("Zava.Plugins", ("lib/net462/Zava.Plugins.dll", "a"), ("lib/net462/Other.dll", "b"));
        var temps = new List<string>();

        ExtractedPackage package = PackageScanner.ExtractPackage(nupkg, "Zava.Plugins", temps);

        Assert.Equal("Zava.Plugins.dll", Path.GetFileName(package.PrimaryAssemblyPath));
        CleanUp(temps, nupkg);
    }

    [Fact]
    public void ExtractPackage_PrefersNamedAssembly_WhenPreferredNameHasDllSuffix()
    {
        string nupkg = BuildNupkg("Zava.Plugins", ("lib/net462/Zava.Plugins.dll", "a"), ("lib/net462/Other.dll", "b"));
        var temps = new List<string>();

        ExtractedPackage package = PackageScanner.ExtractPackage(nupkg, "Zava.Plugins.dll", temps);

        Assert.Equal("Zava.Plugins.dll", Path.GetFileName(package.PrimaryAssemblyPath));
        CleanUp(temps, nupkg);
    }

    [Fact]
    public void ExtractPackage_FallsBackToPackageId_WhenNoPreferredNameGiven()
    {
        // No preferred name -> the nuspec id drives the "<id>.dll" primary choice.
        string nupkg = BuildNupkg("Zava.Plugins", ("lib/net462/Zava.Plugins.dll", "a"), ("lib/net462/Aaa.Extra.dll", "b"));
        var temps = new List<string>();

        ExtractedPackage package = PackageScanner.ExtractPackage(nupkg, null, temps);

        Assert.Equal("Zava.Plugins.dll", Path.GetFileName(package.PrimaryAssemblyPath));
        CleanUp(temps, nupkg);
    }

    [Fact]
    public void ExtractPackage_ExcludesFrameworkAssemblies_FromRegistrablePaths()
    {
        string nupkg = BuildNupkg(
            "Zava.Plugins",
            ("lib/net462/Zava.Plugins.dll", "a"),
            ("lib/net462/Microsoft.Xrm.Sdk.dll", "b"),
            ("lib/net462/System.Text.Json.dll", "c"));
        var temps = new List<string>();

        ExtractedPackage package = PackageScanner.ExtractPackage(nupkg, "Zava.Plugins", temps);

        string[] registrable = package.RegistrableAssemblyPaths.Select(Path.GetFileName).ToArray()!;
        Assert.Contains("Zava.Plugins.dll", registrable);
        Assert.DoesNotContain("Microsoft.Xrm.Sdk.dll", registrable);
        Assert.DoesNotContain("System.Text.Json.dll", registrable);
        CleanUp(temps, nupkg);
    }

    [Fact]
    public void ExtractPackage_IgnoresDllsOutsideLibFolder()
    {
        // A .dll that is not under lib/ must not be extracted, so it cannot become the primary assembly.
        string nupkg = BuildNupkg("Zava.Plugins", ("lib/net462/Zava.Plugins.dll", "a"), ("content/Stray.dll", "b"));
        var temps = new List<string>();

        ExtractedPackage package = PackageScanner.ExtractPackage(nupkg, "Zava.Plugins", temps);

        string[] extracted = Directory.GetFiles(package.TempDirectory).Select(Path.GetFileName).ToArray()!;
        Assert.Contains("Zava.Plugins.dll", extracted);
        Assert.DoesNotContain("Stray.dll", extracted);
        CleanUp(temps, nupkg);
    }

    [Fact]
    public void ExtractPackage_Throws_WhenNoAssembliesInPackage()
    {
        string nupkg = BuildNupkg("Zava.Plugins", ("content/readme.txt", "hi"));
        var temps = new List<string>();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PackageScanner.ExtractPackage(nupkg, null, temps));

        Assert.Contains("No assemblies", ex.Message);
        CleanUp(temps, nupkg);
    }

    // Writes a minimal .nupkg (a zip with a nuspec plus the given entries) to a temp path and returns it.
    private static string BuildNupkg(string packageId, params (string Path, string Content)[] entries)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-scan-{Guid.NewGuid():N}.nupkg");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            ZipArchiveEntry nuspec = zip.CreateEntry($"{packageId}.nuspec");
            using (Stream s = nuspec.Open())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(
                    $"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">" +
                    $"<metadata><id>{packageId}</id><version>1.2.3</version></metadata></package>");
                s.Write(bytes, 0, bytes.Length);
            }

            foreach ((string entryPath, string content) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(entryPath);
                using Stream s = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        return path;
    }

    private static void CleanUp(List<string> tempDirectories, string nupkg)
    {
        foreach (string dir in tempDirectories)
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
                // Best-effort cleanup; the OS temp folder is reclaimed regardless.
            }
        }

        try
        {
            File.Delete(nupkg);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
