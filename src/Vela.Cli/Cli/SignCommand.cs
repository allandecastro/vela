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
    // The `vela sign` allowlist (NOT the args forwarded to `dotnet nuget sign`, e.g. --certificate-* / --overwrite,
    // which are internal). Value options consume the next token; --json stands alone.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--package", "--fingerprint", "--pfx", "--pfx-base64", "--pfx-password", "--pfx-credential", "--timestamp",
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--json", "--help", "-h",
    };

    public static int Run(string[] args)
    {
        if (IsHelpRequested(args))
        {
            PrintUsage();
            return ExitSuccess;
        }

        // Fail fast on a misspelled/unknown option before touching the package.
        if (!TryValidateKnownOptions(args, ValueOptions, FlagOptions, out int optExit))
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
        string? pfxBase64 = TryReadOption(args, "--pfx-base64");
        string? pfxPassword = TryReadOption(args, "--pfx-password");
        string? pfxCredential = TryReadOption(args, "--pfx-credential");

        bool hasFingerprint = !string.IsNullOrWhiteSpace(fingerprint);
        bool hasPfx = !string.IsNullOrWhiteSpace(pfxPath);
        bool hasPfxBase64 = !string.IsNullOrWhiteSpace(pfxBase64);

        // Exactly one certificate source: a fingerprint (local store), a .pfx file, or base64 .pfx content
        // (the pipeline case, where the certificate arrives as a secret rather than a file on disk).
        int sources = (hasFingerprint ? 1 : 0) + (hasPfx ? 1 : 0) + (hasPfxBase64 ? 1 : 0);
        if (sources == 0)
        {
            exitCode = Fail("Provide one certificate source: --fingerprint <hex>, --pfx <path>, or --pfx-base64 <content>.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        if (sources > 1)
        {
            exitCode = Fail("Use only one certificate source (--fingerprint, --pfx, or --pfx-base64).", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        if ((hasPfx || hasPfxBase64) && string.IsNullOrWhiteSpace(pfxPassword) && string.IsNullOrWhiteSpace(pfxCredential))
        {
            exitCode = Fail("When signing with a .pfx, provide --pfx-password or --pfx-credential.", ExitInvalidArguments, includeUsage: false);
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
            Json: HasFlag(args, "--json"));
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
                        File.WriteAllBytes(tempPfx, bytes);
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
                if (string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(options.PfxCredentialTarget))
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        return Fail("--pfx-credential reads the Windows Credential Manager and is supported on Windows only.", ExitParseError, includeUsage: false);
                    }

                    password = WindowsCredential.ReadPassword(options.PfxCredentialTarget!);
                    if (string.IsNullOrEmpty(password))
                    {
                        return Fail($"Credential '{options.PfxCredentialTarget}' not found in Windows Credential Manager, or its password is empty.", ExitParseError, includeUsage: false);
                    }
                }

                if (string.IsNullOrWhiteSpace(password))
                {
                    return Fail("When signing with a .pfx, provide --pfx-password or --pfx-credential.", ExitInvalidArguments, includeUsage: false);
                }

                string source = tempPfx is null ? $"'{pfxFile}'" : "--pfx-base64";
                if (!options.Json) { Console.WriteLine($"Signing '{options.PackagePath}' with PFX {source} ..."); }
                psi.ArgumentList.Add("--certificate-path");
                psi.ArgumentList.Add(pfxFile);
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
                return Fail("Failed to start 'dotnet nuget sign'.", ExitParseError, includeUsage: false);
            }

            // Read both streams to completion before waiting, so the child can't block on a full output buffer.
            string standardError = process.StandardError.ReadToEnd();
            _ = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                // Surface the child's stderr (the actual signing error) — it carries the failure reason, not the
                // certificate or password (those are passed as arguments, never echoed back).
                string detail = string.IsNullOrWhiteSpace(standardError) ? string.Empty : " " + standardError.Trim();
                return Fail($"dotnet nuget sign failed (exit {process.ExitCode}).{detail}", ExitParseError, includeUsage: false);
            }

            Console.WriteLine(options.Json ? BuildSignReport(options) : $"Signed: {options.PackagePath}");
            return ExitSuccess;
        }
        catch (Exception ex)
        {
            return Fail($"dotnet nuget sign failed: {ex.Message}", ExitParseError, includeUsage: false);
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
    bool Json = false)
{
    public const string DefaultTimestampUrl = "http://timestamp.digicert.com";
}
