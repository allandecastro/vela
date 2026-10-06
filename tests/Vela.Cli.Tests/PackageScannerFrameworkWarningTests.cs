using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// Writes to Console.Error, so it runs in the console-redirect collection (no parallel console swaps).
[Collection("ConsoleRedirect")]
public sealed class PackageScannerFrameworkWarningTests : IDisposable
{
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
        }
    }

    private string Nupkg(string entry)
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-tfm-" + Guid.NewGuid().ToString("N") + ".nupkg");
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (Stream s = zip.CreateEntry(entry).Open())
        {
            s.Write(Encoding.UTF8.GetBytes("dll"));
        }

        _paths.Add(path);
        return path;
    }

    private string ExtractCapturingStderr(string nupkg)
    {
        TextWriter original = Console.Error;
        using StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            List<string> temps = [];
            PackageScanner.ExtractPackage(nupkg, "Zava.Plugins", temps);
            _paths.AddRange(temps);
        }
        finally
        {
            Console.SetError(original);
        }

        return captured.ToString();
    }

    [Fact]
    public void A_package_above_net471_warns_that_Dataverse_will_reject_it()
    {
        string stderr = ExtractCapturingStderr(Nupkg("lib/net48/Zava.Plugins.dll"));

        Assert.Contains("targets net48", stderr);
        Assert.Contains("above net471", stderr);
        Assert.Contains("<TargetFramework>net462</TargetFramework>", stderr); // the fix
    }

    [Fact]
    public void A_registrable_framework_does_not_warn()
    {
        Assert.Equal(string.Empty, ExtractCapturingStderr(Nupkg("lib/net462/Zava.Plugins.dll")));
    }
}
