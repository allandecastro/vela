using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Vela.Cli;

/// <summary>
/// Runs the package scan in a child <c>vela</c> process.
/// <para>
/// Reading a package's steps and Custom APIs instantiates its plugin types and calls their definition methods
/// (<see cref="PackageScanner"/>): the scan runs code from the package. In the CLI process, that code would share the
/// CLI's memory: the open Dataverse connection, the access token, the resolved client secret and secure-config values.
/// The child process keeps package code out of that state: it starts with an allowlisted environment (system paths,
/// locale, the .NET runtime variables, so no credential is inherited by accident), no stdin, and only the package
/// paths. Its stdout is forwarded to stderr, so package output can never corrupt the JSON on stdout.
/// </para>
/// <para>
/// This is NOT a sandbox. The child runs with the user's OS rights: hostile package code can still read the parent's
/// command line and environment (<c>ps</c>, <c>/proc/&lt;pid&gt;/cmdline</c> and <c>/proc/&lt;pid&gt;/environ</c> on
/// Linux), the user's files (including the interactive token cache under <c>~/.vela</c>) and the network. Only scan
/// packages you build or trust. <c>VELA_SCAN_IN_PROCESS=1</c> restores the in-process scan (for debugging).
/// </para>
/// </summary>
internal static class IsolatedPackageScan
{
    /// <summary>The hidden command the child runs.</summary>
    internal const string ChildCommand = "__scan";

    internal const string InProcessVariable = "VELA_SCAN_IN_PROCESS";

    private const int ChildTimeoutMinutes = 10;

    // Environment variables passed to the child, by exact name (case-insensitive) or prefix. Everything else, in
    // particular DATAVERSE_*, VELA_SECURE_CONFIG_*, AZURE_*, IDENTITY_*/MSI_* and CI tokens, is not inherited (the
    // child could still read them from the parent through the OS; see the class remarks: not a sandbox).
    private static readonly string[] AllowedVariables =
    [
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TMPDIR", "TMP", "TEMP", "LANG", "TERM", "TZ",
        "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "CommonProgramW6432", "NUMBER_OF_PROCESSORS", "OS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
        "USERNAME", "USERDOMAIN", "COMPUTERNAME", "HOMEDRIVE", "HOMEPATH",
        "NO_COLOR", "VELA_NO_COLOR",
    ];

    private static readonly string[] AllowedPrefixes = ["DOTNET_", "LC_", "XDG_"];

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.General);

    /// <summary>The package code runs here: what the child receives.</summary>
    internal sealed record ScanRequest(string[] PackagePaths, string? PackageAssembly);

    /// <summary>What the child returns: the local state, or the scan failure (type name + message).</summary>
    internal sealed record ScanResult(RegistrationState? State, string? ErrorType, string? Error);

    /// <summary>
    /// Scans in a child process (the default) and adds the web resources in this process (reading files runs no
    /// package code). The internal overload's <c>command</c> overrides how the child is started (tests).
    /// </summary>
    public static RegistrationState Scan(SharedOptions options, JsonSerializerOptions jsonOptions, List<string> tempDirectories) =>
        Scan(options, jsonOptions, tempDirectories, command: null, Environment.GetEnvironmentVariable);

    internal static RegistrationState Scan(SharedOptions options, JsonSerializerOptions jsonOptions, List<string> tempDirectories,
        (string FileName, string[] PrefixArgs)? command, Func<string, string?> getVariable)
    {
        if (string.Equals(getVariable(InProcessVariable), "1", StringComparison.Ordinal))
        {
            return PackageScanner.BuildLocalStateFromPackages(options, jsonOptions, tempDirectories);
        }

        RegistrationState state = options.PackagePaths.Length == 0
            ? new RegistrationState([], [], [], DateTime.UtcNow, [], [])
            : RunInChild(new ScanRequest(options.PackagePaths, options.PackageAssembly), command ?? ResolveSelfCommand());

        if (string.IsNullOrWhiteSpace(options.WebResourcesRoot))
        {
            return state;
        }

        // Stryker disable all : web resources are a work-in-progress preview, not mutation-gated yet
        RegistrationItem[] items = [.. state.Items, .. WebResourceScanner.Scan(options.WebResourcesRoot!, jsonOptions)];
        return state with
        {
            Items = items.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray(),
        };
        // Stryker restore all
    }

    /// <summary>The child entry point: <c>vela __scan --request &lt;file&gt; --result &lt;file&gt;</c>.</summary>
    public static int RunChild(string[] args)
    {
        string? requestPath = CommandLineOptions.TryReadOption(args, "--request");
        string? resultPath = CommandLineOptions.TryReadOption(args, "--result");
        if (requestPath == null || resultPath == null)
        {
            Console.Error.WriteLine($"Usage: vela {ChildCommand} --request <file> --result <file> (internal).");
            return CliExitCodes.ExitInvalidArguments;
        }

        ScanResult result;
        List<string> tempDirectories = [];
        try
        {
            ScanRequest request = JsonSerializer.Deserialize<ScanRequest>(File.ReadAllText(requestPath), Wire)
                ?? throw new InvalidOperationException("Empty scan request.");
            SharedOptions options = new(request.PackagePaths, request.PackageAssembly, null, null, null, null, null, null,
                AutoApprove: false, Interactive: false, Strict: false, Verbose: false);
            // The state's definitions are serialized with the CLI's own JSON options, exactly as in-process.
            JsonSerializerOptions jsonOptions = new()
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            };
            result = new ScanResult(PackageScanner.BuildLocalStateFromPackages(options, jsonOptions, tempDirectories), null, null);
        }
        catch (Exception ex)
        {
            result = new ScanResult(null, ex.GetType().Name, ex.Message);
        }
        finally
        {
            PackageScanner.CleanupTempDirectories(tempDirectories);
        }

        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, Wire));
        return result.State != null ? CliExitCodes.ExitSuccess : CliExitCodes.ExitError;
    }

    // How to start this CLI again: the apphost (vela / Vela.Cli.exe) directly, or `dotnet Vela.Cli.dll` when the
    // process is the dotnet muxer.
    internal static (string FileName, string[] PrefixArgs) ResolveSelfCommand()
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException($"Cannot locate the vela executable to run the isolated package scan. Set {InProcessVariable}=1 to scan in-process.");
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string entry = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException($"Cannot locate Vela.Cli.dll to run the isolated package scan. Set {InProcessVariable}=1 to scan in-process.");
            return (processPath, [entry]);
        }

        return (processPath, []);
    }

    /// <summary>The environment the child starts with: only the allowlisted variables of <paramref name="current"/>.</summary>
    internal static Dictionary<string, string> BuildChildEnvironment(IEnumerable<KeyValuePair<string, string>> current)
    {
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in current)
        {
            bool allowed = AllowedVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
                || AllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (allowed)
            {
                environment[name] = value;
            }
        }

        // The child must never look for a newer CLI or prompt.
        environment["VELA_NO_UPDATE_CHECK"] = "1";
        return environment;
    }

    private static RegistrationState RunInChild(ScanRequest request, (string FileName, string[] PrefixArgs) command)
    {
        string workDir = Path.Combine(Path.GetTempPath(), "vela-tools", "scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            string requestPath = Path.Combine(workDir, "request.json");
            string resultPath = Path.Combine(workDir, "result.json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request, Wire));

            ProcessStartInfo psi = new(command.FileName)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
            };
            foreach (string arg in command.PrefixArgs.Concat([ChildCommand, "--request", requestPath, "--result", resultPath]))
            {
                psi.ArgumentList.Add(arg);
            }

            psi.Environment.Clear();
            IEnumerable<KeyValuePair<string, string>> current = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Select(e => new KeyValuePair<string, string>((string)e.Key, (string?)e.Value ?? string.Empty));
            foreach ((string name, string value) in BuildChildEnvironment(current))
            {
                psi.Environment[name] = value;
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Could not start the isolated package scan ({command.FileName}).");
            process.StandardInput.Close();
            // Both streams go to OUR stderr: warnings the scan prints (and anything the package writes) never reach stdout.
            Task forwardOut = ForwardAsync(process.StandardOutput);
            Task forwardErr = ForwardAsync(process.StandardError);
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(ChildTimeoutMinutes).TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"The package scan did not finish within {ChildTimeoutMinutes} minutes; aborted.");
            }

            Task.WaitAll(forwardOut, forwardErr);
            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException($"The isolated package scan exited with code {process.ExitCode} without a result.");
            }

            ScanResult result = JsonSerializer.Deserialize<ScanResult>(File.ReadAllText(resultPath), Wire)
                ?? throw new InvalidOperationException("The isolated package scan returned an empty result.");
            return result.State ?? throw Rehydrate(result);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort: the work directory holds only the request/result JSON
            }
        }
    }

    private static readonly object ForwardLock = new();

    // Writes through Console.Error (not the raw handle) so a redirected stderr sees the lines; the two streams are
    // forwarded concurrently, hence the lock.
    private static async Task ForwardAsync(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            lock (ForwardLock)
            {
                Console.Error.WriteLine(line);
            }
        }
    }

    // Maps the child's failure back to the exception type the exit-code mapping expects (missing file = 3, else 4).
    internal static Exception Rehydrate(ScanResult result) => result.ErrorType switch
    {
        nameof(FileNotFoundException) => new FileNotFoundException(result.Error),
        nameof(DirectoryNotFoundException) => new DirectoryNotFoundException(result.Error),
        _ => new InvalidOperationException(result.Error ?? "The package scan failed."),
    };
}
