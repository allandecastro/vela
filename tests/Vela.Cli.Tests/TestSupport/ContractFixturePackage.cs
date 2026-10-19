using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace Vela.Cli.Tests.TestSupport;

/// <summary>
/// Packs the built <c>tests/ContractPluginFixture</c> (a net462 plugin assembly on the real Vela.Plugins) into a
/// plugin .nupkg the way a consumer's build ships it: the plugin assembly and its dependencies under
/// <c>lib/net462</c>, without the Dataverse SDK assemblies the platform provides.
/// </summary>
internal static class ContractFixturePackage
{
    internal const string PackageId = "contract_ContractPluginFixture";
    internal const string AssemblyName = "ContractPluginFixture";

    // The plugin assembly and the dependencies it ships with. The SDK (Microsoft.Xrm.Sdk, Microsoft.Crm.Sdk.Proxy) and
    // the System.* facades are provided by the host, so they are not packed (the scanner resolves them from the CLI).
    private static readonly string[] PackedAssemblies =
    [
        "ContractPluginFixture.dll",
        "Vela.Plugins.dll",
        "Vela.Messages.dll",
        "Newtonsoft.Json.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
    ];

    internal static string FixtureDirectory =>
        typeof(ContractFixturePackage).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ContractPluginFixtureDir").Value!;

    /// <summary>Writes the package to a new temp file and returns its path; the caller deletes it.</summary>
    internal static string Build(string version = "1.0.0", bool withoutPlugins = false)
    {
        string dir = FixtureDirectory;
        string nupkg = Path.Combine(Path.GetTempPath(), $"{PackageId}.{Guid.NewGuid():N}.nupkg");
        using FileStream fs = new(nupkg, FileMode.CreateNew);
        using ZipArchive zip = new(fs, ZipArchiveMode.Create);

        ZipArchiveEntry nuspec = zip.CreateEntry($"{PackageId}.nuspec");
        using (Stream s = nuspec.Open())
        {
            byte[] xml = Encoding.UTF8.GetBytes(
                $"<?xml version=\"1.0\"?><package><metadata><id>{PackageId}</id><version>{version}</version></metadata></package>");
            s.Write(xml, 0, xml.Length);
        }

        foreach (string file in PackedAssemblies)
        {
            if (withoutPlugins && file == $"{AssemblyName}.dll")
            {
                // The plugin project after its last plugin class was deleted: same assembly name, no type at all.
                ZipArchiveEntry empty = zip.CreateEntry("lib/net462/" + file);
                using Stream es = empty.Open();
                EmptyAssembly(AssemblyName).Save(es);
                continue;
            }

            string path = Path.Combine(dir, file);
            Assert.True(File.Exists(path), $"contract fixture output not found: {path} (build tests/ContractPluginFixture)");
            zip.CreateEntryFromFile(path, "lib/net462/" + file);
        }

        return nupkg;
    }

    private static System.Reflection.Emit.PersistedAssemblyBuilder EmptyAssembly(string name)
    {
        System.Reflection.Emit.PersistedAssemblyBuilder builder = new(new AssemblyName(name), typeof(object).Assembly);
        builder.DefineDynamicModule(name);
        return builder;
    }

    internal static SharedOptions Options(IReadOnlyList<string> packages, string? outPath = null, bool detailedExitCode = false) => new(
        PackagePaths: [.. packages],
        PackageAssembly: AssemblyName,
        ConnectionString: null,
        OAuthUrl: null,
        OAuthToken: null,
        SolutionUniqueName: null,
        Environment: null,
        OutPath: outPath,
        AutoApprove: true,
        Interactive: false,
        Strict: false,
        Verbose: false,
        DetailedExitCode: detailedExitCode);
}
