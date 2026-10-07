using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>
/// Implements <c>vela sign</c>: NuGet-signs a built plugin package (<c>.nupkg</c>) by shelling out to
/// <c>dotnet nuget sign</c>. The generated Integrations build target calls this after packing so the
/// deployed package carries a trusted signature (needed for managed-identity plugins).
/// </summary>
internal static class SignCommand
{
    // The `vela sign` allowlist, from the command table (NOT the args forwarded to `dotnet nuget sign`, e.g.
    // --certificate-* / --overwrite, which are internal).
    private static readonly HashSet<string> ValueOptions = CliCommands.ValueOptions("sign");

    private static readonly HashSet<string> FlagOptions = CliCommands.FlagOptions("sign");

    public static int Run(string[] args)
    {
        if (IsHelpRequested(args))
        {
            CliCommands.PrintCommandHelp("sign");
            return ExitSuccess;
        }

        // Fail fast on a misspelled/unknown option before touching the package.
        if (!TryTokenize(args, ValueOptions, FlagOptions, out args, out int optExit))
        {
            return optExit;
        }

        if (!TryParseOptions(args, out SignOptions options, out int exitCode))
        {
            return exitCode;
        }

        return Execute(options);
    }

    /// <summary>
    /// Parses and validates the option combination (pure; no file-system access) so it can be unit-tested.
    /// File existence and the actual signing happen in <see cref="Execute"/>.
    /// </summary>
    public static bool TryParseOptions(string[] args, out SignOptions options, out int exitCode)
    {
        options = default!;
        exitCode = ExitSuccess;

        string? package = TryReadOption(args, "--package");
        if (string.IsNullOrWhiteSpace(package))
        {
            exitCode = Fail("Missing package to sign. Use --package <path.nupkg>.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        string? fingerprint = TryReadOption(args, "--fingerprint");
        string? pfxPath = TryReadOption(args, "--pfx");
        string? pfxCredential = TryReadOption(args, "--pfx-credential");

        // The two secrets can come from a named environment variable instead of the command line, where other users
        // of a shared host can read them (ps / /proc). The literal options stay for compatibility, with a warning.
        if (!TryReadSecret(args, "--pfx-base64", out string? pfxBase64, out bool base64OnCommandLine, out exitCode)
            || !TryReadSecret(args, "--pfx-password", out string? pfxPassword, out bool passwordOnCommandLine, out exitCode))
        {
            return false;
        }

        bool hasFingerprint = !string.IsNullOrWhiteSpace(fingerprint);
        bool hasPfx = !string.IsNullOrWhiteSpace(pfxPath);
        bool hasPfxBase64 = !string.IsNullOrWhiteSpace(pfxBase64);

        // Exactly one certificate source: a fingerprint (local store), a .pfx file, or base64 .pfx content
        // (the pipeline case, where the certificate arrives as a secret rather than a file on disk).
        int sources = (hasFingerprint ? 1 : 0) + (hasPfx ? 1 : 0) + (hasPfxBase64 ? 1 : 0);
        if (sources == 0)
        {
            exitCode = Fail("Provide one certificate source: --fingerprint <hex>, --pfx <path>, or --pfx-base64-env <VAR> (or --pfx-base64 <content>).", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        if (sources > 1)
        {
            exitCode = Fail("Use only one certificate source (--fingerprint, --pfx, or --pfx-base64).", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        if ((hasPfx || hasPfxBase64) && string.IsNullOrWhiteSpace(pfxPassword) && string.IsNullOrWhiteSpace(pfxCredential))
        {
            exitCode = Fail("When signing with a .pfx, provide --pfx-password-env <VAR>, --pfx-password or --pfx-credential.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        string timestamp = TryReadOption(args, "--timestamp") ?? SignOptions.DefaultTimestampUrl;

        options = new SignOptions(
            PackagePath: package,
            Fingerprint: fingerprint,
            PfxPath: pfxPath,
            PfxBase64: pfxBase64,
            PfxPassword: pfxPassword,
            PfxCredentialTarget: pfxCredential,
            TimestampUrl: timestamp,
            Json: HasFlag(args, "--json"),
            SecretsOnCommandLine: base64OnCommandLine || passwordOnCommandLine);
        return true;
    }

    // Reads a secret from `<option> <value>` or from the environment variable named by `<option>-env <NAME>`. Both at
    // once is ambiguous; a named variable that is unset or empty is an error rather than a silent "no secret".
    private static bool TryReadSecret(string[] args, string option, out string? value, out bool onCommandLine, out int exitCode)
    {
        exitCode = ExitSuccess;
        string? literal = TryReadOption(args, option);
        string? variable = TryReadOption(args, option + "-env");
        onCommandLine = !string.IsNullOrWhiteSpace(literal);
        value = literal;
        if (string.IsNullOrWhiteSpace(variable))
        {
            return true;
        }

        if (onCommandLine)
        {
            exitCode = Fail($"Use either {option} or {option}-env, not both.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        value = Environment.GetEnvironmentVariable(variable!.Trim());
        if (string.IsNullOrEmpty(value))
        {
            exitCode = Fail($"{option}-env: the environment variable '{variable}' is not set or is empty.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        return true;
    }

    private static int Execute(SignOptions options)
    {
        if (!File.Exists(options.PackagePath))
        {
            return Fail($"Package to sign not found: {options.PackagePath}", ExitFileNotFound, includeUsage: false);
        }

        // A base64 certificate (pipeline case) is decoded to a temp .pfx, used, then deleted in the finally
        // so the secret never lingers on disk.
        string? tempPfx = null;
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "dotnet",
                UseShellExecute = false,
                // Capture the child's output instead of letting it stream straight to the console — diagnostics
                // are surfaced deliberately (on failure) rather than dumped raw.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("nuget");
            psi.ArgumentList.Add("sign");
            psi.ArgumentList.Add(options.PackagePath);

            if (!string.IsNullOrWhiteSpace(options.Fingerprint))
            {
                if (!options.Json) { Console.WriteLine($"Signing '{options.PackagePath}' with certificate fingerprint {options.Fingerprint} ..."); }
                psi.ArgumentList.Add("--certificate-fingerprint");
                psi.ArgumentList.Add(options.Fingerprint);
            }
            else
            {
                string pfxFile;
                if (!string.IsNullOrWhiteSpace(options.PfxBase64))
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(options.PfxBase64!.Trim());
                        tempPfx = Path.Combine(Path.GetTempPath(), $"vela-sign-{Guid.NewGuid():N}.pfx");
                        // The decoded PFX carries the private key: create it owner-only from the start.
                        WriteOwnerOnlyFile(tempPfx, bytes);
                        // Wipe the decoded certificate from the managed heap once it's on disk — it's no longer
                        // needed in memory and shouldn't linger until GC.
                        Array.Clear(bytes, 0, bytes.Length);
                    }
                    catch (FormatException)
                    {
                        return Fail("--pfx-base64 is not valid base64.", ExitInvalidArguments, includeUsage: false);
                    }

                    pfxFile = tempPfx;
                }
                else
                {
                    if (!File.Exists(options.PfxPath))
                    {
                        return Fail($"PFX file not found: {options.PfxPath}", ExitFileNotFound, includeUsage: false);
                    }

                    pfxFile = options.PfxPath!;
                }

                string? password = options.PfxPassword;
                if (options.SecretsOnCommandLine)
                {
                    // A literal --pfx-password / --pfx-base64 sits in this process's argv, readable by other users on a
                    // shared host (ps / /proc). Steer to the -env variants, mirroring the --client-secret warning. (#307)
                    Console.Error.WriteLine(
                        "Warning: --pfx-password / --pfx-base64 on the command line can be read by other users on a shared host (ps/proc). Pass them through an environment variable with --pfx-password-env <VAR> / --pfx-base64-env <VAR>, or prefer --fingerprint (an installed certificate).");
                }

                if (string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(options.PfxCredentialTarget))
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        return Fail("--pfx-credential reads the Windows Credential Manager and is supported on Windows only.", ExitError, includeUsage: false);
                    }

                    password = WindowsCredential.ReadPassword(options.PfxCredentialTarget!);
                    if (string.IsNullOrEmpty(password))
                    {
                        return Fail($"Credential '{options.PfxCredentialTarget}' not found in Windows Credential Manager, or its password is empty.", ExitError, includeUsage: false);
                    }
                }

                if (string.IsNullOrWhiteSpace(password))
                {
                    return Fail("When signing with a .pfx, provide --pfx-password-env <VAR>, --pfx-password or --pfx-credential.", ExitInvalidArguments, includeUsage: false);
                }

                string source = tempPfx is null ? $"'{pfxFile}'" : "--pfx-base64";
                if (!options.Json) { Console.WriteLine($"Signing '{options.PackagePath}' with PFX {source} ..."); }
                psi.ArgumentList.Add("--certificate-path");
                psi.ArgumentList.Add(pfxFile);
                // 'dotnet nuget sign' accepts the PFX password ONLY as this argv argument — there is no env-var or
                // stdin alternative. On a multi-user host the value is visible to other users via 'ps'/proc for the
                // duration of the child process. This cannot be remediated without replacing the signing mechanism;
                // callers sharing a host should prefer --certificate-fingerprint (an installed cert) over --pfx-password.
                psi.ArgumentList.Add("--certificate-password");
                psi.ArgumentList.Add(password!);
            }

            // RFC-3161 timestamp so the signature stays valid after the certificate expires; --overwrite lets
            // a rebuilt package be re-signed without failing on an existing signature.
            psi.ArgumentList.Add("--timestamper");
            psi.ArgumentList.Add(options.TimestampUrl);
            psi.ArgumentList.Add("--overwrite");

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return Fail("Failed to start 'dotnet nuget sign'.", ExitError, includeUsage: false);
            }

            // Drain both streams CONCURRENTLY so the child can't block writing to one pipe while we read the other
            // to completion (reading stderr fully before touching stdout is the classic deadlock).
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            string standardError = errorTask.GetAwaiter().GetResult();
            _ = outputTask.GetAwaiter().GetResult();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                // Surface the child's stderr (the actual signing error) — it carries the failure reason, not the
                // certificate or password (those are passed as arguments, never echoed back).
                string detail = string.IsNullOrWhiteSpace(standardError) ? string.Empty : " " + standardError.Trim();
                return Fail($"dotnet nuget sign failed (exit {process.ExitCode}).{detail}", ExitError, includeUsage: false);
            }

            if (options.Json)
            {
                Console.WriteLine(BuildSignReport(options));
            }
            else
            {
                ConsoleTheme.WriteSuccessLine($"Signed: {options.PackagePath}");
            }
            return ExitSuccess;
        }
        catch (Exception ex)
        {
            return Fail($"dotnet nuget sign failed: {ex.Message}", ExitError, includeUsage: false);
        }
        finally
        {
            if (tempPfx is not null)
            {
                try
                {
                    File.Delete(tempPfx);
                }
                catch
                {
                    // Best-effort cleanup of the decoded certificate; a leaked temp file is non-fatal.
                }
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to a NEW file readable only by its owner. On Unix the mode is set at creation
    /// (no window where the decoded private key is world-readable, unlike write-then-chmod); on Windows the file
    /// lands under the per-user temp folder, whose ACL already restricts it to the user. Fails if the file exists.
    /// </summary>
    internal static void WriteOwnerOnlyFile(string path, byte[] bytes)
    {
        FileStreamOptions streamOptions = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using FileStream stream = new(path, streamOptions);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Reads a generic credential's password from the Windows Credential Manager (the <c>--pfx-credential</c>
    /// path). Returns null when the credential is absent.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class WindowsCredential
    {
        private const int CredTypeGeneric = 1;

        public static string? ReadPassword(string target)
        {
            if (!NativeMethods.CredRead(target, CredTypeGeneric, 0, out IntPtr credentialPtr) || credentialPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                NativeMethods.CREDENTIAL credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credentialPtr);
                if (credential.CredentialBlobSize <= 0 || credential.CredentialBlob == IntPtr.Zero)
                {
                    return null;
                }

                // The blob is a Unicode string; CredentialBlobSize is in bytes.
                return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
            }
            finally
            {
                NativeMethods.CredFree(credentialPtr);
            }
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct CREDENTIAL
            {
                public int Flags;
                public int Type;
                public string TargetName;
                public string Comment;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
                public int CredentialBlobSize;
                public IntPtr CredentialBlob;
                public int Persist;
                public int AttributeCount;
                public IntPtr Attributes;
                public string TargetAlias;
                public string UserName;
            }

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern void CredFree(IntPtr credentialPtr);
        }
    }

    // The --json success report: the signed package, the method used and the timestamp authority.
    internal static string BuildSignReport(SignOptions options) =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                package = options.PackagePath,
                signed = true,
                method = options.Fingerprint != null ? "fingerprint" : "pfx",
                timestampUrl = options.TimestampUrl,
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}

/// <summary>Parsed options for <c>vela sign</c>.</summary>
internal sealed record SignOptions(
    string PackagePath,
    string? Fingerprint,
    string? PfxPath,
    string? PfxBase64,
    string? PfxPassword,
    string? PfxCredentialTarget,
    string TimestampUrl,
    bool Json = false,
    bool SecretsOnCommandLine = false)
{
    public const string DefaultTimestampUrl = "http://timestamp.digicert.com";
}
