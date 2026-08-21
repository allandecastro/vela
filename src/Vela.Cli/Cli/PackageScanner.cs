using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
    Dictionary<string, string> secureConfigByStepKey = new(StringComparer.Ordinal);

    foreach (string packagePath in options.PackagePaths)
    {
        packagePayloads.Add(ReadPluginPackagePayload(packagePath));
        ExtractedPackage package = ExtractPackage(packagePath, options.PackageAssembly, tempDirectories);
        // Loads the package's assembly (and resolves its sibling dependencies from the same temp folder).
        // The types below are then instantiated and invoked to read their definitions — so this runs code from
        // the package. Only scan trusted packages. See the class-level SECURITY note.
        Assembly assembly = Assembly.LoadFrom(package.PrimaryAssemblyPath);
        string assemblyName = assembly.GetName().Name ?? assembly.FullName ?? Path.GetFileNameWithoutExtension(packagePath);
        sourceAssemblies.Add(assemblyName);
        foreach (string assemblyPath in package.RegistrableAssemblyPaths)
        {
            string payloadAssemblyName = AssemblyName.GetAssemblyName(assemblyPath).Name ?? Path.GetFileNameWithoutExtension(assemblyPath);
            sourceAssemblyPayloads.Add(new AssemblyPayload(payloadAssemblyName, assemblyPath));
        }

        foreach (Type type in GetLoadableTypes(assembly))
        {
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
                    // Dataverse supports no image on the CreateMultiple/UpdateMultiple messages (it accepts
                    // neither 'Target' nor 'Targets' as an image messagepropertyname), so a bulk-message step
                    // with a pre/post image cannot be deployed. The in-memory harness allows it (to simulate
                    // per-record bulk images), so reject it here at scan time - before any write - with a clear
                    // message rather than letting apply fail mid-run with a cryptic platform error.
                    if (IsBulkStepWithImages(normalized))
                    {
                        string bulkMessage = normalized.TryGetProperty("message", out JsonElement bm) ? bm.GetString() ?? "" : "";
                        throw new InvalidOperationException(
                            $"Plugin '{type.FullName}' registers a step on '{bulkMessage}' with a pre/post image, but " +
                            "Dataverse supports no images on the CreateMultiple/UpdateMultiple messages. Register the " +
                            "image on the single Create/Update step instead (it also runs for bulk operations, where " +
                            "per-record images are available by index), or remove the image from the bulk step.");
                    }
                    // Items keep only the hash of the secure config (so secrets never reach plan/inspect output);
                    // stash the RAW value here, keyed by stepKey, so apply can write the real secret to Dataverse.
                    if (raw.TryGetProperty("secureConfig", out JsonElement secureEl) && secureEl.ValueKind == JsonValueKind.String
                        && secureEl.GetString() is { Length: > 0 } rawSecureConfig
                        && normalized.TryGetProperty("stepKey", out JsonElement keyEl) && keyEl.GetString() is { Length: > 0 } stepKey)
                    {
                        secureConfigByStepKey[stepKey] = rawSecureConfig;
                    }

                    items.Add(new RegistrationItem("step", type.FullName ?? type.Name, normalized));
                }
            }

            if (IsPluginType(type, "CustomApiBase"))
            {
                if (!TryGetDefinition(type, "GetCustomApiDefinition", out object? definition, out string? error))
                {
                    throw new InvalidOperationException(BuildIntrospectionFailure(packagePath, type, "the Custom API definition", error));
                }

                JsonElement normalized = NormalizeCustomApiDefinition(JsonSerializer.SerializeToElement(definition, jsonOptions), jsonOptions);
                items.Add(new RegistrationItem("custom-api", type.FullName ?? type.Name, normalized));
            }
        }
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
        SecureConfigByStepKey: secureConfigByStepKey);
}

    // True when a step targets a bulk message (CreateMultiple/UpdateMultiple) and carries any pre/post image.
    // Dataverse cannot deploy such a step, so plan/apply reject it up front (see the call site).
    internal static bool IsBulkStepWithImages(JsonElement step)
    {
        string? message = step.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
        bool isBulk = string.Equals(message, "CreateMultiple", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message, "UpdateMultiple", StringComparison.OrdinalIgnoreCase);
        if (!isBulk)
        {
            return false;
        }

        return HasItems(step, "preImages") || HasItems(step, "postImages");

        static bool HasItems(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0;
    }

    // A type we identified as a plugin/Custom API but couldn't introspect is a fatal authoring error: continuing
    // would treat it as contributing nothing, so plan/apply would mark its existing live registrations for deletion.
    private static string BuildIntrospectionFailure(string packagePath, Type type, string what, string? error)
{
    return $"Failed to read {what} from '{type.FullName ?? type.Name}' in package '{Path.GetFileName(packagePath)}': {error} " +
        "Fix the plugin so it can be read — refusing to continue, since otherwise its existing registrations could be deleted.";
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

    List<string> extractedDllPaths = [];
    foreach (ZipArchiveEntry entry in archive.Entries)
    {
        if (!entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) ||
            !entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
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
        return ex.Types.Where(x => x != null)!;
    }
}

    internal static bool IsPluginType(Type type, string baseTypeName)
{
    if (!type.IsClass || type.IsAbstract)
    {
        return false;
    }

    Type? current = type.BaseType;
    while (current != null)
    {
        if (current.Name.StartsWith(baseTypeName, StringComparison.Ordinal))
        {
            return true;
        }

        current = current.BaseType;
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

    try
    {
        object? instance = ctor.Invoke([null, null]);
        object? result = method.Invoke(instance, null);
        if (result == null)
        {
            error = $"Method '{methodName}' returned null.";
            return false;
        }

        if (result is System.Array arr)
        {
            definitions = arr.Cast<object>().ToArray();
            if (definitions.Length == 0)
            {
                error = $"Method '{methodName}' returned an empty array.";
                return false;
            }
            return true;
        }

        definitions = [result];
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

    try
    {
        object? instance = ctor.Invoke([null, null]);
        definition = method.Invoke(instance, null);
        if (definition == null)
        {
            error = $"Method '{methodName}' returned null.";
            return false;
        }

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
