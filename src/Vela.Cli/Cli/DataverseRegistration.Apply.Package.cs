using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.ServiceModel;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;


namespace Vela.Cli;

/// <summary> The plugin package publish: the create/update/skip decision shared by plan and apply, the prefix repack and the stored-file compare. </summary>
internal static partial class DataverseRegistration
{
    internal static void EnsurePluginPackages(IOrganizationService service, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories, bool verbose)
{
    // Nothing to publish (a web-resource-only run, or a run that only fixes solution membership): no prefix lookup.
    if (packagePayloads.Length == 0)
    {
        return;
    }

    EnsurePluginPackagesImportable(packagePayloads, solutionUniqueName);

    string? solutionPrefix = string.IsNullOrWhiteSpace(solutionUniqueName)
        ? null
        : ResolveSolutionCustomizationPrefix(service, solutionUniqueName);

    // Solution membership comes from the caller's solution-scoped service (the SolutionUniqueName request
    // parameter); the prefix only shapes the package name.
    foreach (PluginPackagePayload package in packagePayloads)
    {
        UpsertPluginPackage(service, package, solutionPrefix, tempDirectories, verbose);
    }
}

    internal static Guid UpsertPluginPackage(IOrganizationService service, PluginPackagePayload package, string? solutionPrefix, List<string> tempDirectories, bool verbose)
{
    if (!File.Exists(package.PackagePath))
    {
        throw new FileNotFoundException($"Plugin package file not found: {package.PackagePath}", package.PackagePath);
    }

    string desiredPackageName = NormalizePackageNameWithPrefix(package.PackageName, solutionPrefix);
    byte[] packageBytes = ReadPackageBytesForImport(package.PackagePath, package.PackageName, desiredPackageName);
    string packageContent = Convert.ToBase64String(packageBytes);
    Entity? existing = GetPluginPackage(service, desiredPackageName);
    if (existing == null && !package.HasRegistrations)
    {
        // Nothing to register yet (an empty scaffolded project): creating the package would only add an empty
        // component to Dataverse. Same decision as ComputePackagePublishActions; Guid.Empty means "not written".
        LogOperation("package", "skip", desiredPackageName, "no plugin or Custom API", verbose);
        return Guid.Empty;
    }

    if (existing == null)
    {
        LogOperation("package", "create", desiredPackageName, $"v{package.Version}", verbose);
        Entity create = new Entity("pluginpackage");
        create["name"] = desiredPackageName;
        create["version"] = package.Version;
        create["content"] = packageContent;
        return WithPackageFrameworkHint(package, desiredPackageName, () => service.Create(create));
    }

    // Skip a no-op Update: if the stored package is already this one, updating would only bump modifiedon (Last
    // Modified) and re-import the package on every apply, making it look churned when nothing changed.
    if (PluginPackageMatches(service, existing, packageBytes))
    {
        LogOperation("package", "unchanged", desiredPackageName, $"v{package.Version}", verbose);
        return existing.Id;
    }

    LogOperation("package", "update", desiredPackageName, $"v{package.Version}", verbose);
    Entity update = new Entity("pluginpackage")
    {
        Id = existing.Id
    };
    // Only write content: an Update that sets content and a version other than the stored one fails with
    // "PluginPackage version cannot be updated" (verified live), which used to fail apply mid-run whenever the
    // package version was bumped. The stored version therefore keeps its first value (see PluginPackageMatches).
    update["content"] = packageContent;
    return WithPackageFrameworkHint(package, desiredPackageName, () =>
    {
        service.Update(update);
        return existing.Id;
    });
}

    // Dataverse supports plug-ins up to .NET Framework 4.8, and its error for a package above that (net481) does not
    // name the target framework. When the package targets above net48, say so and give the way out. ServiceClient
    // often wraps the fault, so the exception chain is walked (as IsIgnorableCustomApiStageException does).
    private static Guid WithPackageFrameworkHint(PluginPackagePayload package, string packageName, Func<Guid> write)
{
    try
    {
        return write();
    }
    catch (Exception ex)
        when (FindOrganizationFault(ex) is { } fault
            && PackageScanner.PackageTargetFramework(package.PackagePath) is { } tfm && PackageScanner.ExceedsSupportedFramework(tfm))
    {
        throw new InvalidOperationException(
            $"Dataverse rejected plugin package '{packageName}', which targets {tfm}: {fault.Message} " +
            $"Dataverse supports plug-ins up to .NET Framework 4.8 ({PackageScanner.SupportedFrameworkMax}). " +
            $"Set <TargetFramework>{PackageScanner.SupportedFrameworkMax}</TargetFramework> in the plugin project and rebuild.", ex);
    }
}

    private static FaultException<OrganizationServiceFault>? FindOrganizationFault(Exception ex)
{
    for (Exception? current = ex; current != null; current = current.InnerException)
    {
        if (current is FaultException<OrganizationServiceFault> fault)
        {
            return fault;
        }
    }

    return null;
}

    // Read-only counterpart of EnsurePluginPackages: true if any plugin package would be created or republished.
    // The registration diff only covers steps/custom APIs, so a code-only change (same step and custom-API
    // definitions, but a rebuilt assembly) is otherwise invisible and apply would report "no changes" and deploy
    // nothing. Mirrors UpsertPluginPackage's create/skip decision exactly (same prefix, same import bytes, same
    // stored-file compare) without writing.
    internal static bool PluginPackagesNeedPublish(IOrganizationService service, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories)
        => ComputePackagePublishActions(service, packagePayloads, solutionUniqueName, tempDirectories).Count > 0;

    // Per-package detail behind PluginPackagesNeedPublish: one action for every package that would be created
    // (absent from Dataverse) or republished (stored file differs); packages whose stored file already matches are
    // omitted. Lets plan list the package operation and flag HasChanges for a
    // code-only rebuild. Mirrors UpsertPluginPackage exactly so plan and apply never disagree: same create/skip
    // decision, same missing-file FileNotFoundException. The importable-prefix guard (EnsurePluginPackagesImportable)
    // runs separately in both paths, so an unprefixed package without --solution never reaches here as a "create".
    // A package without registrations that is absent from Dataverse is neither listed nor created; it is named in
    // `info` instead. One that already exists is compared and republished as usual (removing its last plugin still
    // updates it).
    internal static IReadOnlyList<PackagePublishAction> ComputePackagePublishActions(IOrganizationService service, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories, List<string>? info = null)
{
    if (packagePayloads.Length == 0)
    {
        return [];
    }

    string? solutionPrefix = string.IsNullOrWhiteSpace(solutionUniqueName)
        ? null
        : ResolveSolutionCustomizationPrefix(service, solutionUniqueName);

    List<PackagePublishAction> actions = [];
    foreach (PluginPackagePayload package in packagePayloads)
    {
        string desiredPackageName = NormalizePackageNameWithPrefix(package.PackageName, solutionPrefix);

        if (!File.Exists(package.PackagePath))
        {
            // Match UpsertPluginPackage: a missing package file is an error, not a silent "no change". Throwing
            // here keeps plan and apply in step (plan fails with the same message instead of advertising an
            // operation apply cannot perform).
            throw new FileNotFoundException($"Plugin package file not found: {package.PackagePath}", package.PackagePath);
        }

        byte[] packageBytes = ReadPackageBytesForImport(package.PackagePath, package.PackageName, desiredPackageName);
        Entity? existing = GetPluginPackage(service, desiredPackageName);
        if (existing == null && !package.HasRegistrations)
        {
            info?.Add($"Plugin package '{desiredPackageName}' contains no plugin or Custom API; it is not created in Dataverse until it does.");
            continue;
        }

        if (existing == null)
        {
            actions.Add(new PackagePublishAction(desiredPackageName, IsCreate: true));
            continue;
        }

        // Same decision as UpsertPluginPackage: update unless the stored package is already this one.
        if (!PluginPackageMatches(service, existing, packageBytes))
        {
            actions.Add(new PackagePublishAction(desiredPackageName, IsCreate: false));
        }
    }

    return actions;
}

    internal static string NormalizePackageNameWithPrefix(string packageName, string? solutionPrefix)
{
    if (string.IsNullOrWhiteSpace(solutionPrefix) || packageName.StartsWith(solutionPrefix + "_", StringComparison.OrdinalIgnoreCase))
    {
        return packageName;
    }

    return $"{solutionPrefix}_{packageName}";
}

    internal static byte[] ReadPackageBytesForImport(string packagePath, string originalPackageName, string desiredPackageName)
{
    if (string.Equals(originalPackageName, desiredPackageName, StringComparison.OrdinalIgnoreCase))
    {
        return File.ReadAllBytes(packagePath);
    }

    // Applying with --solution injects the publisher prefix by rewriting the package id in the .nuspec and
    // repacking. That repack changes the archive content, which invalidates any NuGet author signature: Dataverse
    // then rejects the upload with 0x80040265 "the package is signed but its signature does not cover the package
    // content". Detect a signed package up front and fail with an actionable message instead of a cryptic
    // platform error. (The package must be named with the prefix BEFORE signing.)
    if (IsPackageSigned(packagePath))
    {
        throw new InvalidOperationException(
            $"Plugin package '{originalPackageName}' is signed, but applying it with a solution requires renaming it " +
            $"to '{desiredPackageName}' (to add the publisher prefix), which would invalidate the signature and be " +
            "rejected by Dataverse (0x80040265). Build/sign the package already named with the solution publisher " +
            $"prefix ('{desiredPackageName}').");
    }

    return RepackWithPackageId(File.ReadAllBytes(packagePath), packagePath, desiredPackageName);
}

    // Rewrites the package id deterministically, in memory: every entry is copied in its original order with its
    // original timestamp, and only the .nuspec is replaced (renamed to the new id, keeping its timestamp). The same
    // input therefore always yields the same bytes, so PluginPackagesNeedPublish sees an unchanged package as
    // unchanged; extracting and re-zipping stamped every entry with the current time, so the package was re-imported
    // on every apply.
    private static readonly XmlWriterSettings NuspecWriterSettings = new()
    {
        Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
    };

    internal static byte[] RepackWithPackageId(byte[] packageBytes, string packagePath, string desiredPackageName)
{
    using MemoryStream input = new(packageBytes, writable: false);
    using ZipArchive source = new(input, ZipArchiveMode.Read);
    ZipArchiveEntry nuspecEntry = source.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"No .nuspec found in package '{packagePath}'.");

    XDocument nuspec;
    using (Stream nuspecStream = nuspecEntry.Open())
    {
        nuspec = XDocument.Load(nuspecStream);
    }

    XNamespace ns = nuspec.Root?.Name.Namespace ?? XNamespace.None;
    XElement idElement = nuspec.Root?.Element(ns + "metadata")?.Element(ns + "id")
        ?? throw new InvalidOperationException($"Package '{packagePath}' is missing metadata id in nuspec.");
    idElement.Value = desiredPackageName;

    using MemoryStream output = new();
    using (ZipArchive target = new(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (ZipArchiveEntry entry in source.Entries)
        {
            bool isNuspec = ReferenceEquals(entry, nuspecEntry);
            ZipArchiveEntry copy = target.CreateEntry(isNuspec ? $"{desiredPackageName}.nuspec" : entry.FullName, CompressionLevel.Optimal);
            copy.LastWriteTime = entry.LastWriteTime;
            using Stream destination = copy.Open();
            if (isNuspec)
            {
                // Explicit encoding and newlines: XDocument.Save's defaults follow the platform (CRLF on Windows), so
                // the same package repacked on Linux and Windows runners would differ and be re-imported.
                using XmlWriter writer = XmlWriter.Create(destination, NuspecWriterSettings);
                nuspec.Save(writer);
            }
            else
            {
                using Stream content = entry.Open();
                content.CopyTo(destination);
            }
        }
    }

    return output.ToArray();
}

    // A NuGet-signed package carries a top-level '.signature.p7s' entry (the PKCS#7 signature). Its presence means
    // any modification to the archive (e.g. the prefix-rename repack) invalidates the signature.
    internal static bool IsPackageSigned(string packagePath)
{
    using ZipArchive archive = ZipFile.OpenRead(packagePath);
    return archive.GetEntry(".signature.p7s") != null;
}

    // Whether the stored package is already these bytes. Dataverse keeps the uploaded .nupkg in the `package` file
    // column and empties `content` once it has processed it (verified live: content reads back empty right after the
    // create, and the package file is byte-identical to the upload), so the file is what is compared. Its size is
    // checked first, so a changed package is usually told apart without a download. The stored `version` is not
    // compared: a content update leaves it at its first value (verified live: 1.0.0 after 1.0.1 was uploaded), so
    // every plan after a version bump would propose the update again. The bytes include the .nuspec, so a version
    // bump still shows as a change.
    internal static bool PluginPackageMatches(IOrganizationService service, Entity existing, byte[] packageBytes)
{
    InitializeFileBlocksDownloadResponse download;
    try
    {
        download = (InitializeFileBlocksDownloadResponse)service.Execute(new InitializeFileBlocksDownloadRequest
        {
            Target = new EntityReference("pluginpackage", existing.Id),
            FileAttributeName = "package",
        });
    }
    catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail?.ErrorCode == ObjectDoesNotExistErrorCode)
    {
        return false; // no stored file ("No FileAttachment records found"): publishing stores one
    }

    if (download.FileSizeInBytes != packageBytes.LongLength)
    {
        return false;
    }

    // Without chunking support Dataverse serves the file only as one block covering the whole file (as
    // FileColumnExtensions.DownloadFile does), so the 4 MB block size applies to chunked downloads only. An absent flag
    // means chunking is supported.
    bool chunked = !download.Results.Contains("IsChunkingSupported") || download.Results["IsChunkingSupported"] is not bool supported || supported;
    long blockSize = chunked ? PackageDownloadBlockSize : Math.Max(packageBytes.LongLength, 1);

    long offset = 0;
    while (offset < packageBytes.LongLength)
    {
        long length = Math.Min(blockSize, packageBytes.LongLength - offset);
        DownloadBlockResponse block = (DownloadBlockResponse)service.Execute(new DownloadBlockRequest
        {
            FileContinuationToken = download.FileContinuationToken,
            Offset = offset,
            BlockLength = length,
        });
        byte[] data = block.Data ?? [];
        if (data.Length == 0 || data.Length > length || !data.AsSpan().SequenceEqual(packageBytes.AsSpan((int)offset, data.Length)))
        {
            return false;
        }

        offset += data.Length;
    }

    return true;
}

    // The largest block a chunked DownloadBlock serves (4 MB).
    private const long PackageDownloadBlockSize = 4L * 1024 * 1024;

    internal static Entity? GetPluginPackage(IOrganizationService service, string packageName, bool withManagedIdentity = false)
{
    QueryExpression query = new QueryExpression("pluginpackage")
    {
        // Neither content nor version is read: content reads back empty and version keeps its first value
        // (PluginPackageMatches compares the stored file instead). The managed identity link only for --managed-identity.
        ColumnSet = withManagedIdentity ? new ColumnSet("pluginpackageid", "name", "managedidentityid") : new ColumnSet("pluginpackageid", "name"),
        TopCount = 1
    };
    query.Criteria.AddCondition("name", ConditionOperator.Equal, packageName);
    return service.RetrieveMultiple(query).Entities.FirstOrDefault();
}
}
