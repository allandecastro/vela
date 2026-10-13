using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// A-45: the package scan runs package code, so it runs in a child vela process that gets no credentials. These run
/// the real child (the Vela.Cli apphost next to the tests) against compiled fixture plugins.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class IsolatedPackageScanTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<string> _files = [];
    private readonly List<string> _temps = [];

    private static (string FileName, string[] PrefixArgs) Child()
    {
        string apphost = Path.Combine(AppContext.BaseDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Vela.Cli.exe" : "Vela.Cli");
        Assert.True(File.Exists(apphost), $"Vela.Cli apphost not found next to the tests: {apphost}");
        return (apphost, []);
    }

    private static SharedOptions Options(params string[] packages) => new(packages, null, "AuthType=ClientSecret;ClientSecret=top-secret", null, null, null, null, null,
        AutoApprove: false, Interactive: false, Strict: false, Verbose: false);

    private static string? NotInProcess(string _) => null;

    [Fact]
    public void The_child_scan_returns_the_same_state_as_the_in_process_scan()
    {
        string nupkg = BuildNupkg("SamplePluginFixture.dll", "SamplePluginFixture");
        RegistrationState inProcess = PackageScanner.BuildLocalStateFromPackages(Options(nupkg), Web, _temps);

        RegistrationState isolated = IsolatedPackageScan.Scan(Options(nupkg), Web, _temps, Child(), NotInProcess);

        Assert.NotEmpty(isolated.Items);
        Assert.Equal(JsonSerializer.Serialize(inProcess.Items), JsonSerializer.Serialize(isolated.Items));
        Assert.Equal(inProcess.SourceAssemblies, isolated.SourceAssemblies);
        Assert.Equal(inProcess.PackagePayloads, isolated.PackagePayloads);
        Assert.Equal(inProcess.UnmanagedPluginTypes, isolated.UnmanagedPluginTypes);
        Assert.NotEmpty(isolated.TypedCustomApiUniqueNames!);
        Assert.Equal(inProcess.TypedCustomApiUniqueNames, isolated.TypedCustomApiUniqueNames);
        Assert.Equal(inProcess.SecureConfigNameByStepKey, isolated.SecureConfigNameByStepKey);
    }

    [Fact]
    public void A_package_the_child_cannot_read_fails_with_the_scan_message()
    {
        string nupkg = BuildNupkg("BrokenPluginFixture.dll", "BrokenPluginFixture");

        var ex = Assert.Throws<InvalidOperationException>(() => IsolatedPackageScan.Scan(Options(nupkg), Web, _temps, Child(), NotInProcess));

        Assert.Contains("Missing (string, string) constructor.", ex.Message);
    }

    [Fact]
    public void A_missing_package_keeps_the_file_not_found_exit_code()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.nupkg");

        Assert.Throws<FileNotFoundException>(() => IsolatedPackageScan.Scan(Options(missing), Web, _temps, Child(), NotInProcess));
    }

    [Fact]
    public void No_package_starts_no_child()
    {
        RegistrationState state = IsolatedPackageScan.Scan(Options(), Web, _temps, ("/nonexistent/vela", []), NotInProcess);

        Assert.Empty(state.Items);
        Assert.Empty(state.PackagePayloads);
    }

    [Fact]
    public void The_in_process_opt_out_scans_without_a_child()
    {
        string nupkg = BuildNupkg("SamplePluginFixture.dll", "SamplePluginFixture");

        // A child command that cannot start proves the child is not used.
        RegistrationState state = IsolatedPackageScan.Scan(Options(nupkg), Web, _temps, ("/nonexistent/vela", []),
            name => name == IsolatedPackageScan.InProcessVariable ? "1" : null);

        Assert.NotEmpty(state.Items);
    }

    [Fact]
    public void The_child_environment_carries_no_credential()
    {
        var current = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/me",
            ["DOTNET_ROOT"] = "/usr/share/dotnet",
            ["lc_all"] = "C",
            ["DATAVERSE_CLIENT_SECRET"] = "s1",
            ["DATAVERSE_CONNECTION_STRING"] = "ClientSecret=s2",
            ["VELA_SECURE_CONFIG_PaymentKey"] = "s3",
            ["AZURE_CLIENT_SECRET"] = "s4",
            ["IDENTITY_HEADER"] = "s5",
            ["ACTIONS_ID_TOKEN_REQUEST_TOKEN"] = "s6",
            ["SYSTEM_ACCESSTOKEN"] = "s7",
            ["GITHUB_TOKEN"] = "s8",
        };

        Dictionary<string, string> child = IsolatedPackageScan.BuildChildEnvironment(current);

        Assert.Equal("/usr/bin", child["PATH"]);
        Assert.Equal("/home/me", child["HOME"]);
        Assert.Equal("/usr/share/dotnet", child["DOTNET_ROOT"]);
        Assert.Equal("C", child["lc_all"]);
        Assert.Equal("1", child["VELA_NO_UPDATE_CHECK"]);
        Assert.Equal(5, child.Count);
        Assert.DoesNotContain(child.Values, v => v.StartsWith('s') || v.Contains("Secret", StringComparison.Ordinal));
    }

    [Fact]
    public void RunChild_without_its_files_is_an_invalid_invocation()
    {
        TextWriter original = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            Assert.Equal(CliExitCodes.ExitInvalidArguments, IsolatedPackageScan.RunChild(["--request", "only"]));
            Assert.Equal(CliExitCodes.ExitInvalidArguments, IsolatedPackageScan.RunChild(["--result", "only"]));
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public void RunChild_writes_the_failure_to_the_result_file()
    {
        string request = TempFile(JsonSerializer.Serialize(new IsolatedPackageScan.ScanRequest([Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}.nupkg")], null)));
        string result = TempFile(string.Empty);

        int exit = IsolatedPackageScan.RunChild(["--request", request, "--result", result]);

        Assert.Equal(CliExitCodes.ExitError, exit);
        IsolatedPackageScan.ScanResult? parsed = JsonSerializer.Deserialize<IsolatedPackageScan.ScanResult>(File.ReadAllText(result));
        Assert.Null(parsed!.State);
        Assert.Equal(nameof(FileNotFoundException), parsed.ErrorType);
    }

    [Theory]
    [InlineData(nameof(FileNotFoundException), typeof(FileNotFoundException))]
    [InlineData(nameof(DirectoryNotFoundException), typeof(DirectoryNotFoundException))]
    [InlineData("InvalidOperationException", typeof(InvalidOperationException))]
    [InlineData(null, typeof(InvalidOperationException))]
    public void Rehydrate_restores_the_exception_kind(string? type, Type expected)
    {
        Exception ex = IsolatedPackageScan.Rehydrate(new IsolatedPackageScan.ScanResult(null, type, "msg"));

        Assert.IsType(expected, ex);
        Assert.Equal("msg", ex.Message);
    }

    [Fact]
    public void Rehydrate_without_a_message_has_a_default()
        => Assert.Equal("The package scan failed.", IsolatedPackageScan.Rehydrate(new IsolatedPackageScan.ScanResult(null, null, null)).Message);

    private string TempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-scan-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }

    private string BuildNupkg(string dllFileName, string packageId)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, dllFileName);
        string nupkg = Path.Combine(Path.GetTempPath(), $"{packageId}.{Guid.NewGuid():N}.nupkg");
        _files.Add(nupkg);
        using ZipArchive zip = ZipFile.Open(nupkg, ZipArchiveMode.Create);
        using (Stream s = zip.CreateEntry($"{packageId}.nuspec").Open())
        {
            byte[] xml = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><package><metadata><id>{packageId}</id><version>1.0.0</version></metadata></package>");
            s.Write(xml, 0, xml.Length);
        }

        zip.CreateEntryFromFile(dll, "lib/netstandard2.0/" + dllFileName);
        return nupkg;
    }

    public void Dispose()
    {
        foreach (string file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }

        PackageScanner.CleanupTempDirectories(_temps);
    }
}
