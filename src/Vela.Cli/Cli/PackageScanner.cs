using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using static Vela.Cli.JsonReader;
using static Vela.Cli.DefinitionNormalizer;
using static Vela.Cli.DiffEngine;

namespace Vela.Cli;

/// <summary>
/// Builds local registration state by extracting plugin .nupkg packages and reflecting over their
/// assemblies to discover plugin steps and custom API definitions.
/// <para>
/// SECURITY: discovering steps/Custom APIs requires <b>instantiating the plugin types and invoking</b>
/// <c>GetStepDefinitions()</c>/<c>GetCustomApiDefinition()</c> (see <see cref="TryGetDefinitions"/>), so
/// scanning <b>runs code from the package</b>. This is inherent — the definitions are produced by the plugin
/// itself. Therefore only ever scan packages you build or otherwise trust; a hostile <c>.nupkg</c> could run
/// arbitrary code at scan time. An isolating <c>AssemblyLoadContext</c> would not change this (the code still
/// runs); trust of the package is the actual control.
/// </para>
/// </summary>
internal static class PackageScanner
{
    // Exercised end-to-end by PackageScannerBuildStateTests, which Assembly.LoadFrom's a real compiled plugin
    // fixture out of a .nupkg (happy path, the BuildIntrospectionFailure branch, and dedup/ordering of the state).
    internal static RegistrationState BuildLocalStateFromPackages(SharedOptions options, JsonSerializerOptions jsonOptions, List<string> tempDirectories)
{
    List<RegistrationItem> items = [];
    List<string> sourceAssemblies = [];
    List<AssemblyPayload> sourceAssemblyPayloads = [];
    List<PluginPackagePayload> packagePayloads = [];
    Dictionary<string, string> secureConfigNameByStepKey = new(StringComparer.Ordinal);
    List<string> unmanagedPluginTypes = [];

    // An isolated load context that resolves the package's assemblies (and their siblings) from the temp
    // folders by *bytes*, so no .dll file is ever locked - on Windows Assembly.LoadFrom would hold the file
    // for the process lifetime, leaking the temp directory and failing its cleanup. Framework/SDK assemblies
    // not present in the temp folders fall back to the default context.
    PluginLoadContext loadContext = new(tempDirectories);
    try
    {
    // --packages-dir can hold several builds of the same package (1.0.0 and 1.0.1 side by side). Scanning both
    // produced two copies of every step ("Duplicate local step key") and kept whichever version the disk listed first;
    // only the highest version of each package id is scanned, and the others are named on stderr.
    foreach (string packagePath in SelectLatestPackageVersions(options.PackagePaths, Console.Error.WriteLine))
    {
        packagePayloads.Add(ReadPluginPackagePayload(packagePath));
        ExtractedPackage package = ExtractPackage(packagePath, options.PackageAssembly, tempDirectories);
        // Loads the package's assembly (and resolves its sibling dependencies from the same temp folder), by
        // bytes so the file stays deletable. The types below are then instantiated and invoked to read their
        // definitions - so this runs code from the package. Only scan trusted packages. See the SECURITY note.
        Assembly assembly = loadContext.LoadFromAssemblyPathByBytes(package.PrimaryAssemblyPath);
        string assemblyName = assembly.GetName().Name ?? assembly.FullName ?? Path.GetFileNameWithoutExtension(packagePath);
        sourceAssemblies.Add(assemblyName);
        foreach (string assemblyPath in package.RegistrableAssemblyPaths)
        {
            string payloadAssemblyName = AssemblyName.GetAssemblyName(assemblyPath).Name ?? Path.GetFileNameWithoutExtension(assemblyPath);
            sourceAssemblyPayloads.Add(new AssemblyPayload(payloadAssemblyName, assemblyPath));
        }

        foreach (Type type in GetLoadableTypes(assembly))
        {
            if (IsUnmanagedPluginType(type))
            {
                unmanagedPluginTypes.Add(type.FullName ?? type.Name);
                continue;
            }

            if (IsPluginType(type, "EntityPluginBase"))
            {
                if (!TryGetDefinitions(type, "GetStepDefinitions", out object[]? definitions, out string? error))
                {
                    throw new InvalidOperationException(BuildIntrospectionFailure(packagePath, type, "steps", error));
                }

                foreach (object def in definitions!)
                {
                    JsonElement raw = JsonSerializer.SerializeToElement(def, jsonOptions);
                    JsonElement normalized = NormalizeStepDefinition(raw, jsonOptions);
                    // Reject an image Dataverse would refuse (a post-image before PostOperation, a pre-image on Create,
                    // any image on RouteTo, ...) at scan time, before any write, with a clear message rather than
                    // letting apply fail mid-run with a cryptic platform error. Same rules as the runtime builder.
                    if (GetInvalidImageError(normalized) is { } imageError)
                    {
                        throw new InvalidOperationException($"Plugin '{type.FullName}' {imageError}");
                    }
                    if (DataverseRegistration.GetStepDescriptionLengthError(DefinitionParser.ParseStepDefinition(normalized)) is { } descriptionError)
                    {
                        throw new InvalidOperationException($"Plugin '{type.FullName}' {descriptionError}");
                    }
                    RegistrationItem item = new("step", type.FullName ?? type.Name, normalized);
                    // Code names the secure configuration; SecureConfigResolver supplies its value at plan/apply time.
                    // Keyed by the full step key (plugin type + step key), so two plugins sharing a step key never
                    // receive each other's secret.
                    if (raw.TryGetProperty("secureConfigName", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String
                        && nameEl.GetString() is { Length: > 0 } secureConfigName
                        )
                    {
                        secureConfigNameByStepKey[DiffEngine.GetSecureConfigKey(item)] = secureConfigName;
                    }

                    items.Add(item);
                }
            }

            if (IsPluginType(type, "CustomApiBase"))
            {
                if (!TryGetDefinition(type, "GetCustomApiDefinition", out object? definition, out string? error))
                {
                    throw new InvalidOperationException(BuildIntrospectionFailure(packagePath, type, "the Custom API definition", error));
                }

                JsonElement normalized = NormalizeCustomApiDefinition(JsonSerializer.SerializeToElement(definition, jsonOptions), jsonOptions);
                if (DataverseRegistration.GetCustomApiLabelLengthError(DefinitionParser.ParseCustomApiDefinition(normalized)) is { } labelError)
                {
                    throw new InvalidOperationException($"Plugin '{type.FullName}': {labelError}");
                }
                items.Add(new RegistrationItem("custom-api", type.FullName ?? type.Name, normalized));
            }
        }
    }

    // A web resources root adds web-resource items alongside the plugin/Custom API items (folder mirror).
    if (!string.IsNullOrWhiteSpace(options.WebResourcesRoot))
    {
        items.AddRange(WebResourceScanner.Scan(options.WebResourcesRoot!, jsonOptions));
    }

    return new RegistrationState(
        SourceAssemblies: sourceAssemblies.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        SourceAssemblyPayloads: sourceAssemblyPayloads
            .GroupBy(x => x.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.AssemblyName, StringComparer.Ordinal)
            .ToArray(),
        PackagePayloads: packagePayloads
            .GroupBy(x => x.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.PackageName, StringComparer.Ordinal)
            .ToArray(),
        GeneratedAtUtc: DateTime.UtcNow,
        Items: items.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray(),
        Diagnostics: [],
        SecureConfigNameByStepKey: secureConfigNameByStepKey,
        UnmanagedPluginTypes: [.. unmanagedPluginTypes.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)]);
    }
    finally
    {
        // The load context is collectible and the scanned definitions are already serialized to JSON (no live Type
        // references are retained), so request unload to release the package assemblies rather than pinning them for
        // the process lifetime. Unload is a request; collection happens once the runtime drops the last reference.
        loadContext.Unload();
    }
}

    /// <summary>
    /// Keeps one package per package id: the highest version (NuGet ordering: numeric release segments, then a release
    /// above its prereleases). Each skipped package is reported through <paramref name="warn"/>. Order of the kept
    /// packages follows the input.
    /// </summary>
    internal static string[] SelectLatestPackageVersions(IReadOnlyList<string> packagePaths, Action<string> warn)
    {
        List<(string Path, PluginPackagePayload Payload)> packages = packagePaths.Select(p => (p, ReadPluginPackagePayload(p))).ToList();
        HashSet<string> kept = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, (string Path, PluginPackagePayload Payload)> group in packages.GroupBy(p => p.Payload.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            (string Path, PluginPackagePayload Payload) latest = group
                .OrderByDescending(p => p.Payload.Version, PackageVersionComparer.Instance)
                .ThenBy(p => p.Path, StringComparer.Ordinal)
                .First();
            kept.Add(latest.Path);
            foreach ((string Path, PluginPackagePayload Payload) skipped in group.Where(p => !string.Equals(p.Path, latest.Path, StringComparison.Ordinal)))
            {
                warn($"Warning: ignoring '{Path.GetFileName(skipped.Path)}' ({skipped.Payload.PackageName} {skipped.Payload.Version}): " +
                    $"'{Path.GetFileName(latest.Path)}' is a newer version ({latest.Payload.Version}) of the same package.");
            }
        }

        return packagePaths.Where(kept.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Orders NuGet version strings: release segments numerically, a release above its prereleases,
    /// prerelease labels by dot-separated identifier (numeric ones numerically); build metadata is ignored.</summary>
    internal sealed class PackageVersionComparer : IComparer<string>
    {
        public static readonly PackageVersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            (string xRelease, string? xPre) = Split(x);
            (string yRelease, string? yPre) = Split(y);
            int release = CompareIdentifiers(xRelease, yRelease, numericOnly: true);
            if (release != 0)
            {
                return release;
            }

            if (xPre == null || yPre == null)
            {
                return (xPre == null ? 1 : 0) - (yPre == null ? 1 : 0);
            }

            return CompareIdentifiers(xPre, yPre, numericOnly: false);
        }

        private static (string Release, string? Prerelease) Split(string? version)
        {
            string value = (version ?? string.Empty).Split('+')[0];
            int dash = value.IndexOf('-');
            return dash < 0 ? (value, null) : (value.Substring(0, dash), value.Substring(dash + 1));
        }

        private static int CompareIdentifiers(string x, string y, bool numericOnly)
        {
            string[] xs = x.Split('.');
            string[] ys = y.Split('.');
            for (int i = 0; i < Math.Max(xs.Length, ys.Length); i++)
            {
                string a = i < xs.Length ? xs[i] : numericOnly ? "0" : string.Empty;
                string b = i < ys.Length ? ys[i] : numericOnly ? "0" : string.Empty;
                int result = (long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out long na), long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out long nb)) switch
                {
                    (true, true) => na.CompareTo(nb),
                    _ when a.Length == 0 || b.Length == 0 => a.Length.CompareTo(b.Length), // fewer prerelease identifiers sort first
                    (true, false) => -1, // numeric identifiers sort below alphanumeric ones
                    (false, true) => 1,
                    _ => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
                };
                if (result != 0)
                {
                    return Math.Sign(result);
                }
            }

            return 0;
        }
    }

    // Best-effort cleanup of the temp directories created while extracting/transforming packages (see
    // ExtractPackage / DataverseRegistration.ReadPackageBytesForImport). Failures are surfaced to stderr rather
    // than silently swallowed so a leaked directory is visible. Shared by Program (plan/apply/inspect) and
    // `vela models` so the cleanup lives in one place.
    internal static void CleanupTempDirectories(List<string> tempDirectories)
{
    foreach (string directory in tempDirectories)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to delete temp directory '{directory}': {ex.Message}");
        }
    }
}

    // An isolated, byte-loading assembly load context. Loading assemblies from bytes (never LoadFrom /
    // LoadFromAssemblyPath) means the underlying .dll files are never locked, so the extracted temp folders can
    // be deleted right after the scan - on Windows LoadFrom holds the file open for the process lifetime, which
    // leaked the temp directory and made its cleanup warn ("Access to the path '...dll' is denied"). Isolation
    // keeps the package's own dependencies (e.g. the net462 Microsoft.Xrm.Sdk shipped in the package) resolving
    // from the temp folders, matching the previous LoadFrom directory-probing; anything not found there falls
    // back to the default context (framework / SDK assemblies the CLI already has).
    private sealed class PluginLoadContext(IReadOnlyList<string> probeDirectories) : AssemblyLoadContext(isCollectible: true)
    {
        public Assembly LoadFromAssemblyPathByBytes(string assemblyPath)
        {
            using MemoryStream stream = new(File.ReadAllBytes(assemblyPath));
            return LoadFromStream(stream);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName.Name))
            {
                return null;
            }

            foreach (string directory in probeDirectories)
            {
                string candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return LoadFromAssemblyPathByBytes(candidate);
                }
            }

            return null; // framework / SDK assembly -> resolved by the default context
        }
    }

    // Describes the first pre/post image on a normalized step that Dataverse would reject for the step's message and
    // effective stage (see StepImageRules), or returns null when every image is valid.
    internal static string? GetInvalidImageError(JsonElement step)
    {
        string message = step.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : string.Empty;
        int stage = step.TryGetProperty("stage", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
        return Check("preImages", isPreImage: true, "pre-image") ?? Check("postImages", isPreImage: false, "post-image");

        string? Check(string property, bool isPreImage, string kind)
        {
            if (!step.TryGetProperty(property, out JsonElement images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            {
                return null;
            }

            string? reason = Vela.Steps.StepImageRules.GetRejectionReason(message, stage, isPreImage);
            JsonElement offending = images[0];
            if (reason is null)
            {
                // The stage takes this kind of image; each image's request property must also be one the message has.
                foreach (JsonElement image in images.EnumerateArray())
                {
                    string? imageProperty = image.ValueKind == JsonValueKind.Object && image.TryGetProperty("messagePropertyName", out JsonElement p) ? p.GetString() : null;
                    reason = Vela.Steps.StepImageRules.GetMessagePropertyRejectionReason(message, imageProperty);
                    if (reason is not null)
                    {
                        offending = image;
                        break;
                    }
                }

                if (reason is null)
                {
                    return null;
                }
            }

            string alias = offending.ValueKind == JsonValueKind.Object && offending.TryGetProperty("alias", out JsonElement a) ? a.GetString() ?? string.Empty : string.Empty;
            return $"registers a {kind} '{alias}' on a '{message}' step at {(Vela.Constants.PluginStages)stage}, " +
                $"which Dataverse rejects: {reason}. Remove the image or move the step to a stage that supports it.";
        }
    }

    // A type we identified as a plugin/Custom API but couldn't introspect is a fatal authoring error: continuing
    // would treat it as contributing nothing, so plan/apply would mark its existing live registrations for deletion.
    private static string BuildIntrospectionFailure(string packagePath, Type type, string what, string? error)
{
    return $"Failed to read {what} from '{type.FullName ?? type.Name}' in package '{Path.GetFileName(packagePath)}': {error} " +
        "Fix the plugin so it can be read — refusing to continue, since otherwise its existing registrations could be deleted. " +
        "If you recently changed this plugin, the package may be out of date — rebuild it (dotnet build -c Release) and try again.";
}

    internal static PluginPackagePayload ReadPluginPackagePayload(string packagePath)
{
    using ZipArchive archive = ZipFile.OpenRead(packagePath);
    (string? packageId, string? packageVersion) = ReadPackageMetadata(archive);
    string packageName = string.IsNullOrWhiteSpace(packageId)
        ? Path.GetFileNameWithoutExtension(packagePath)
        : packageId!;
    string version = string.IsNullOrWhiteSpace(packageVersion) ? "1.0.0.0" : packageVersion!;
    return new PluginPackagePayload(packageName, version, packagePath);
}

    internal static ExtractedPackage ExtractPackage(string packagePath, string? preferredAssemblyName, List<string> tempDirectories)
{
    string tempRoot = Path.Combine(Path.GetTempPath(), "vela-tools", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    tempDirectories.Add(tempRoot);

    using ZipArchive archive = ZipFile.OpenRead(packagePath);
    string? packageId = ReadPackageId(archive);
    string? preferredFileName = string.IsNullOrWhiteSpace(preferredAssemblyName)
        ? (string.IsNullOrWhiteSpace(packageId) ? null : $"{packageId}.dll")
        : preferredAssemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? preferredAssemblyName : $"{preferredAssemblyName}.dll";

    // A package may ship the same assembly for several target frameworks (lib/net462, lib/netstandard2.0, ...).
    // Flattening every lib/**/*.dll by file name would collide them (last one wins, nondeterministic) and could
    // load a non-net4x build that won't run in the Dataverse sandbox. Pick a single TFM folder (preferring .NET
    // Framework) and extract only its assemblies.
    List<ZipArchiveEntry> libDlls = archive.Entries
        .Where(entry => entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
            && entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .ToList();
    string? chosenTfm = ChooseTargetFramework(libDlls.Select(entry => TargetFrameworkOf(entry.FullName)));
    if (chosenTfm != null && ExceedsPluginPackageFrameworkCap(chosenTfm))
    {
        Console.Error.WriteLine(
            $"Warning: package '{Path.GetFileName(packagePath)}' targets {chosenTfm}. Dataverse rejects plugin packages " +
            $"above {PluginPackageFrameworkCap} at registration (the Microsoft docs recommending net48 do not apply to " +
            "plugin packages). Set <TargetFramework>net462</TargetFramework> in the plugin project and rebuild.");
    }

    List<string> extractedDllPaths = [];
    foreach (ZipArchiveEntry entry in libDlls)
    {
        if (!string.Equals(TargetFrameworkOf(entry.FullName), chosenTfm, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        string destination = Path.Combine(tempRoot, Path.GetFileName(entry.FullName));
        entry.ExtractToFile(destination, overwrite: true);
        extractedDllPaths.Add(destination);
    }

    if (extractedDllPaths.Count == 0)
    {
        throw new InvalidOperationException($"No assemblies found in package '{packagePath}'.");
    }

    string? primary = null;
    if (!string.IsNullOrWhiteSpace(preferredFileName))
    {
        primary = extractedDllPaths.FirstOrDefault(x =>
            string.Equals(Path.GetFileName(x), preferredFileName, StringComparison.OrdinalIgnoreCase));
    }

    if (primary == null)
    {
        primary = extractedDllPaths
            .Where(x => !IsLikelyDependencyAssembly(Path.GetFileNameWithoutExtension(x)))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    primary ??= extractedDllPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First();
    string[] registrableAssemblyPaths = extractedDllPaths
        .Where(x => IsRegistrableAssembly(Path.GetFileNameWithoutExtension(x)))
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return new ExtractedPackage(packagePath, tempRoot, primary, registrableAssemblyPaths);
}

    // "lib/net462/Foo.dll" -> "net462"; a flat "lib/Foo.dll" (no TFM folder) -> "".
    internal static string TargetFrameworkOf(string entryFullName)
{
    string[] parts = entryFullName.Split('/');
    return parts.Length >= 3 ? parts[1] : string.Empty;
}

    // Pick one target framework when a package ships several. The Dataverse sandbox runs .NET Framework, so
    // prefer net4x, then netstandard, then a flat lib, and only then anything else; break ties by the higher
    // version string (net471 over net462). Plugin packages above net471 are rejected at registration (despite the
    // docs recommending net48), so a registrable net4x build ranks above a higher one.
    internal static string? ChooseTargetFramework(IEnumerable<string> targetFrameworks)
{
    List<string> distinct = targetFrameworks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (distinct.Count <= 1)
    {
        return distinct.FirstOrDefault();
    }

    return distinct
        .OrderByDescending(TargetFrameworkRank)
        .ThenByDescending(tfm => tfm, StringComparer.OrdinalIgnoreCase)
        .First();
}

    private static int TargetFrameworkRank(string tfm)
{
    if (tfm.StartsWith("net4", StringComparison.OrdinalIgnoreCase))
    {
        return ExceedsPluginPackageFrameworkCap(tfm) ? 3 : 4;
    }

    if (tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
    {
        return 2;
    }

    return string.IsNullOrEmpty(tfm) ? 1 : 0;
}

    // Dataverse rejects plugin packages whose assemblies target above .NET Framework 4.7.1 (net472, net48, net481),
    // even though the supported-customizations docs list 4.6.2 through 4.8 for plug-ins. Undocumented; observed.
    internal const string PluginPackageFrameworkCap = "net471";

    // "net462" -> 462, "net47" -> 470, "net48" -> 480; compares the digits padded to three places.
    internal static bool ExceedsPluginPackageFrameworkCap(string tfm)
{
    if (!tfm.StartsWith("net4", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    string digits = tfm.Substring(3);
    if (digits.Length == 0 || digits.Length > 3 || !digits.All(char.IsDigit))
    {
        return false;
    }

    return int.Parse(digits.PadRight(3, '0'), CultureInfo.InvariantCulture) > 471;
}

    internal static bool IsLikelyDependencyAssembly(string fileName)
{
    return fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("Newtonsoft.", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("Vela.Plugins", StringComparison.OrdinalIgnoreCase);
}

    internal static bool IsRegistrableAssembly(string fileName)
{
    return !fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) &&
           !fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
}

    internal static string? ReadPackageId(ZipArchive archive)
{
    ZipArchiveEntry? nuspec = archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    if (nuspec == null)
    {
        return null;
    }

    using Stream stream = nuspec.Open();
    XDocument doc = XDocument.Load(stream);
    XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
    return doc.Root?.Element(ns + "metadata")?.Element(ns + "id")?.Value;
}

    internal static (string? packageId, string? packageVersion) ReadPackageMetadata(ZipArchive archive)
{
    ZipArchiveEntry? nuspec = archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    if (nuspec == null)
    {
        return (null, null);
    }

    using Stream stream = nuspec.Open();
    XDocument doc = XDocument.Load(stream);
    XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
    string? packageId = doc.Root?.Element(ns + "metadata")?.Element(ns + "id")?.Value;
    string? packageVersion = doc.Root?.Element(ns + "metadata")?.Element(ns + "version")?.Value;
    return (packageId, packageVersion);
}
    internal static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
{
    try
    {
        return assembly.GetTypes();
    }
    catch (ReflectionTypeLoadException ex)
    {
        // Some types failed to load (typically a dependency missing from the package, or built for a different target
        // framework). Do NOT silently continue with only the types that loaded: a plugin type that vanishes from the
        // scanned state is diffed as 'Removed', and apply would then DELETE its live registrations. Fail the scan
        // loudly so nothing is deleted from a partial or misbuilt package.
        int failed = ex.Types?.Count(t => t is null) ?? 0;
        string? reason = ex.LoaderExceptions?.FirstOrDefault(e => e != null)?.Message;
        throw new InvalidOperationException(
            $"Failed to load {failed} type(s) from assembly '{assembly.GetName().Name}'. This usually means a dependency " +
            "is missing from the package or was built for a different target framework. Aborting the scan so existing " +
            "registrations are not mistakenly deleted." + (reason is null ? string.Empty : $" First loader error: {reason}"),
            ex);
    }
}

    // A registrable class Vela does not own: a concrete IPlugin or CodeActivity that is neither a Vela plugin nor a
    // Vela Custom API (a classic plugin living in the same assembly). Matched by name, since the package's types are
    // loaded in an isolated context. Its live registrations are excluded from the diff (OwnershipScope).
    internal static bool IsUnmanagedPluginType(Type type)
    {
        if (!type.IsClass || type.IsAbstract || IsPluginType(type, "EntityPluginBase") || IsPluginType(type, "CustomApiBase"))
        {
            return false;
        }

        try
        {
            return type.GetInterfaces().Any(i => i.FullName == "Microsoft.Xrm.Sdk.IPlugin")
                || IsPluginType(type, "CodeActivity");
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException)
        {
            // Treating an unclassifiable type as managed would leave its live registrations in the diff, where they
            // read as "removed" and apply deletes them: abort the scan instead, as GetLoadableTypes does.
            throw new InvalidOperationException(
                $"Cannot tell whether '{type.FullName}' is a plugin type: its interfaces fail to load ({ex.Message}). Fix the package's dependencies before planning or applying.", ex);
        }
    }

    internal static bool IsPluginType(Type type, string baseTypeName)
{
    if (!type.IsClass || type.IsAbstract)
    {
        return false;
    }

    for (Type? current = type.BaseType; current != null; current = current.BaseType)
    {
        // Match the base type's simple name EXACTLY (ignoring generic arity, so EntityPluginBase`1 / CustomApiBase`2
        // match), not by prefix. A prefix match treated any type whose base name merely STARTED with the marker - e.g.
        // a user's own "EntityPluginBaseHelper" - as a Vela plugin, a false positive the scanner would then try to
        // instantiate (#305/#303).
        string simpleName = current.Name;
        int arity = simpleName.IndexOf('`');
        if (arity >= 0)
        {
            simpleName = simpleName.Substring(0, arity);
        }

        if (string.Equals(simpleName, baseTypeName, StringComparison.Ordinal))
        {
            return true;
        }
    }

    return false;
}

    internal static bool TryGetDefinitions(Type type, string methodName, out object[]? definitions, out string? error)
{
    definitions = null;
    error = null;

    ConstructorInfo? ctor = type.GetConstructor([typeof(string), typeof(string)]);
    if (ctor == null)
    {
        error = "Missing (string, string) constructor.";
        return false;
    }

    MethodInfo? method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
    if (method == null)
    {
        error = $"Missing public method '{methodName}'.";
        return false;
    }

    if (!TryInvokeWithinTimeout(() => method.Invoke(ctor.Invoke([null, null]), null), methodName, out object? result, out error))
    {
        return false;
    }

    if (result == null)
    {
        error = $"Method '{methodName}' returned null.";
        return false;
    }

    // EntityPluginBase.GetStepDefinitions returns StepDefinition[]. A non-array result is not a Vela plugin (the
    // former single-object fallback served a pre-array framework shape that no longer exists), so fail loudly.
    if (result is not System.Array arr)
    {
        error = $"Method '{methodName}' must return an array of step definitions, but returned '{result.GetType().Name}'.";
        return false;
    }

    definitions = arr.Cast<object>().ToArray();
    if (definitions.Length == 0)
    {
        error = $"Method '{methodName}' returned an empty array.";
        return false;
    }

    return true;
}

    internal static bool TryGetDefinition(Type type, string methodName, out object? definition, out string? error)
{
    definition = null;
    error = null;

    ConstructorInfo? ctor = type.GetConstructor([typeof(string), typeof(string)]);
    if (ctor == null)
    {
        error = "Missing (string, string) constructor.";
        return false;
    }

    MethodInfo? method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
    if (method == null)
    {
        error = $"Missing public method '{methodName}'.";
        return false;
    }

    if (!TryInvokeWithinTimeout(() => method.Invoke(ctor.Invoke([null, null]), null), methodName, out object? result, out error))
    {
        return false;
    }

    if (result == null)
    {
        error = $"Method '{methodName}' returned null.";
        return false;
    }

    definition = result;
    return true;
}

    // The scanner instantiates and invokes code from the package (see the SECURITY note on the class). Run that
    // invocation under a timeout so an infinite loop or a blocking call in a type's constructor/definition method
    // cannot hang the CLI forever (including `vela models --package`); it fails the scan with a clear message
    // instead. A runaway invocation continues on a background thread-pool thread, which does not block process exit.
    private const int PackageCodeTimeoutSeconds = 30;

    private static bool TryInvokeWithinTimeout(Func<object?> invoke, string methodName, out object? result, out string? error)
    {
        result = null;
        error = null;

        Task<object?> task = Task.Run(invoke);

        bool completed;
        try
        {
            // Wait throws AggregateException if the task faulted within the timeout; that means it completed (with an
            // exception), so treat it as completed and let GetResult below rethrow the real, unwrapped exception.
            completed = task.Wait(TimeSpan.FromSeconds(PackageCodeTimeoutSeconds));
        }
        catch (AggregateException)
        {
            completed = true;
        }

        if (!completed)
        {
            error = $"Method '{methodName}' did not complete within {PackageCodeTimeoutSeconds}s " +
                "(possible infinite loop or blocking call in the package); scan aborted for safety.";
            return false;
        }

        try
        {
            result = task.GetAwaiter().GetResult();
            return true;
        }
        catch (TargetInvocationException ex)
        {
            Exception source = ex.InnerException ?? ex;
            error = $"{source.GetType().Name}: {source.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
