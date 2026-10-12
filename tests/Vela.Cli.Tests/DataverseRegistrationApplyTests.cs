using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;
using TestSupport = Vela.Cli.Tests.TestSupport;

namespace Vela.Cli.Tests;

/// <summary>
/// Unit tests for the apply/reconcile helpers in <see cref="DataverseRegistration"/> (the Apply partial).
/// These drive each helper directly through the <see cref="IOrganizationService"/> seam / real file fixtures,
/// asserting the exact queries, written attributes and thrown messages — enough to kill the mutants those
/// lines carried (query attribute/entity names, object initializers, guard branches, exception text).
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class DataverseRegistrationApplyTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    public void Dispose()
    {
        foreach (string path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private string NewTempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-apply-test-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        _tempPaths.Add(path);
        return path;
    }

    private string NewTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-apply-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempPaths.Add(path);
        return path;
    }

    // ---- NormalizePackageNameWithPrefix ---------------------------------------------------------

    [Theory]
    [InlineData("Foo", null, "Foo")]
    [InlineData("Foo", "   ", "Foo")]
    [InlineData("Foo", "new", "new_Foo")]
    [InlineData("new_Foo", "new", "new_Foo")]  // already prefixed → unchanged
    [InlineData("NEW_Foo", "new", "NEW_Foo")]  // already prefixed, different case → unchanged
    [InlineData("newFoo", "new", "new_newFoo")] // starts with prefix but not "prefix_" → still prefixed
    public void NormalizePackageNameWithPrefix_prefixes_only_when_needed(string name, string? prefix, string expected)
        => Assert.Equal(expected, DataverseRegistration.NormalizePackageNameWithPrefix(name, prefix));

    // ---- IsIgnorableCustomApiStageException -----------------------------------------------------

    [Fact]
    public void IsIgnorableCustomApiStageException_true_only_for_the_stage_fault()
    {
        var stageFault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault(), "Invalid plug-in registration stage.");
        Assert.True(DataverseRegistration.IsIgnorableCustomApiStageException(stageFault));

        // Wrapped in an outer operation exception (ServiceClient does this).
        var wrapped = new InvalidOperationException("op failed", stageFault);
        Assert.True(DataverseRegistration.IsIgnorableCustomApiStageException(wrapped));
    }

    [Fact]
    public void IsIgnorableCustomApiStageException_false_for_other_faults_and_plain_exceptions()
    {
        var otherFault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault(), "Some other platform error.");
        Assert.False(DataverseRegistration.IsIgnorableCustomApiStageException(otherFault));

        // A plain exception whose message merely contains the phrase must NOT be swallowed.
        var lookalike = new InvalidOperationException("Invalid plug-in registration stage.");
        Assert.False(DataverseRegistration.IsIgnorableCustomApiStageException(lookalike));
    }

    // ---- GetPluginPackage -----------------------------------------------------------------------

    [Fact]
    public void GetPluginPackage_queries_by_name_and_returns_the_row()
    {
        var existing = new Entity("pluginpackage", Guid.NewGuid());
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };

        Entity? row = DataverseRegistration.GetPluginPackage(svc, "acme_Package");

        Assert.Same(existing, row);
        QueryExpression q = Assert.Single(svc.Queries);
        Assert.Equal("pluginpackage", q.EntityName);
        Assert.Equal(1, q.TopCount);
        Assert.Contains("pluginpackageid", q.ColumnSet.Columns);
        Assert.Contains("name", q.ColumnSet.Columns);
        ConditionExpression c = Assert.Single(q.Criteria.Conditions);
        Assert.Equal("name", c.AttributeName);
        Assert.Equal(ConditionOperator.Equal, c.Operator);
        Assert.Equal("acme_Package", c.Values.Single());
    }

    [Fact]
    public void GetPluginPackage_returns_null_when_absent()
    {
        var svc = new TestSupport.FakeOrgService();
        Assert.Null(DataverseRegistration.GetPluginPackage(svc, "missing"));
    }

    // ---- ResolveSolutionCustomizationPrefix -----------------------------------------------------

    [Fact]
    public void ResolveSolutionCustomizationPrefix_returns_trimmed_prefix()
    {
        var publisherId = Guid.NewGuid();
        var solution = new Entity("solution", Guid.NewGuid())
        {
            ["publisherid"] = new EntityReference("publisher", publisherId),
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(solution),
            OnRetrieve = (entity, id, cols) =>
            {
                Assert.Equal("publisher", entity);
                Assert.Equal(publisherId, id);
                Assert.Contains("customizationprefix", cols.Columns);
                return new Entity("publisher", id) { ["customizationprefix"] = "  new  " };
            },
        };

        Assert.Equal("new", DataverseRegistration.ResolveSolutionCustomizationPrefix(svc, "acme_solution"));

        QueryExpression q = Assert.Single(svc.Queries);
        Assert.Equal("solution", q.EntityName);
        Assert.Contains("publisherid", q.ColumnSet.Columns);
        Assert.Equal("uniquename", q.Criteria.Conditions.Single().AttributeName);
    }

    [Fact]
    public void ResolveSolutionCustomizationPrefix_throws_when_solution_absent()
    {
        var svc = new TestSupport.FakeOrgService();
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.ResolveSolutionCustomizationPrefix(svc, "nope"));
        Assert.Contains("Solution not found", ex.Message);
    }

    [Fact]
    public void ResolveSolutionCustomizationPrefix_throws_when_publisher_missing()
    {
        var svc = new TestSupport.FakeOrgService
        {
            // solution row with no publisherid → publisher id resolves to Guid.Empty
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(new Entity("solution", Guid.NewGuid())),
        };
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.ResolveSolutionCustomizationPrefix(svc, "acme"));
        Assert.Contains("Publisher not found", ex.Message);
    }

    [Fact]
    public void ResolveSolutionCustomizationPrefix_throws_when_prefix_blank()
    {
        var solution = new Entity("solution", Guid.NewGuid())
        {
            ["publisherid"] = new EntityReference("publisher", Guid.NewGuid()),
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(solution),
            OnRetrieve = (entity, id, _) => new Entity(entity, id) { ["customizationprefix"] = "   " },
        };
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.ResolveSolutionCustomizationPrefix(svc, "acme"));
        Assert.Contains("Customization prefix not found", ex.Message);
    }

    // ---- ReadPackageBytesForImport --------------------------------------------------------------

    [Fact]
    public void ReadPackageBytesForImport_returns_raw_bytes_when_name_unchanged()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("nupkg-content");
        string path = NewTempFile(bytes);

        byte[] result = DataverseRegistration.ReadPackageBytesForImport(path, "Same.Name", "Same.Name");

        Assert.Equal(bytes, result);
    }

    [Fact]
    public void ReadPackageBytesForImport_rewrites_nuspec_id_when_renamed()
    {
        string nupkg = BuildNupkg("Old.Name", nuspecId: "Old.Name");

        byte[] result = DataverseRegistration.ReadPackageBytesForImport(nupkg, "Old.Name", "new_Old.Name");

        // The returned package must carry the renamed nuspec id, in a file named after the new id.
        string extractDir = NewTempDir();
        using var stream = new MemoryStream(result);
        ZipFile.ExtractToDirectory(stream, extractDir);
        string[] nuspecs = Directory.GetFiles(extractDir, "*.nuspec", SearchOption.TopDirectoryOnly);
        Assert.Equal("new_Old.Name.nuspec", Path.GetFileName(Assert.Single(nuspecs)));
        XDocumentAssertId(nuspecs[0], "new_Old.Name");
    }

    [Fact]
    public void ReadPackageBytesForImport_repack_is_deterministic_so_an_unchanged_package_is_not_republished()
    {
        // PluginPackagesNeedPublish compares these bytes with the package content in Dataverse: if two repacks of the
        // same package differed (fresh entry timestamps), every 'apply --solution' would re-import the package.
        string nupkg = BuildNupkg("Old.Name", nuspecId: "Old.Name");

        byte[] first = DataverseRegistration.ReadPackageBytesForImport(nupkg, "Old.Name", "new_Old.Name");
        byte[] second = DataverseRegistration.ReadPackageBytesForImport(nupkg, "Old.Name", "new_Old.Name");

        Assert.Equal(first, second);
        // The nuspec is written with explicit LF newlines and UTF-8 without BOM, whatever the platform default.
        using (ZipArchive check = new ZipArchive(new MemoryStream(first)))
        using (StreamReader nuspecReader = new StreamReader(check.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open()))
        {
            string nuspecText = nuspecReader.ReadToEnd();
            Assert.DoesNotContain("\r", nuspecText);
            Assert.False(nuspecText.StartsWith("\uFEFF", StringComparison.Ordinal));
        }

        // Every entry keeps its original timestamp (re-zipping from disk stamped them with the current time).
        using ZipArchive original = ZipFile.OpenRead(nupkg);
        using ZipArchive repacked = new ZipArchive(new MemoryStream(first));
        Assert.Equal(
            original.Entries.Select(e => e.LastWriteTime),
            repacked.Entries.Select(e => e.LastWriteTime));
    }

    [Fact]
    public void ReadPackageBytesForImport_writes_the_nuspec_as_bomless_utf8_with_two_space_indent_and_lf()
    {
        // Read the raw entry bytes (a StreamReader would silently strip a BOM): the rewritten nuspec must be byte-for-
        // byte the same on every platform, so the encoding, indentation and newline settings are all pinned.
        string nupkg = BuildNupkg("Old.Name", nuspecId: "Old.Name");

        byte[] result = DataverseRegistration.ReadPackageBytesForImport(nupkg, "Old.Name", "new_Old.Name");

        using ZipArchive archive = new ZipArchive(new MemoryStream(result));
        using MemoryStream raw = new MemoryStream();
        using (Stream entry = archive.GetEntry("new_Old.Name.nuspec")!.Open())
        {
            entry.CopyTo(raw);
        }

        byte[] bytes = raw.ToArray();
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "nuspec must not carry a UTF-8 BOM");
        string expected =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\">\n" +
            "  <metadata>\n" +
            "    <id>new_Old.Name</id>\n" +
            "    <version>1.0.0</version>\n" +
            "  </metadata>\n" +
            "</package>";
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ReadPackageBytesForImport_throws_when_no_nuspec()
    {
        string nupkg = BuildZip(new Dictionary<string, string> { ["readme.txt"] = "hi" });
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.ReadPackageBytesForImport(nupkg, "A", "b_A"));
        Assert.Contains("No .nuspec found", ex.Message);
    }

    [Fact]
    public void ReadPackageBytesForImport_throws_when_nuspec_missing_id()
    {
        string nuspec = "<package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\"><metadata><version>1.0.0</version></metadata></package>";
        string nupkg = BuildZip(new Dictionary<string, string> { ["pkg.nuspec"] = nuspec });
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.ReadPackageBytesForImport(nupkg, "A", "b_A"));
        Assert.Contains("missing metadata id", ex.Message);
    }

    // ---- UpsertPluginPackage --------------------------------------------------------------------

    [Fact]
    public void UpsertPluginPackage_creates_new_package_with_all_columns()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-bytes");
        string path = NewTempFile(bytes);
        var newId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => new EntityCollection(), // no existing package → create
            OnCreate = _ => newId,
        };

        var payload = new PluginPackagePayload("acme_Pkg", "2.3.4", path);
        Guid result = DataverseRegistration.UpsertPluginPackage(svc, payload, solutionPrefix: null, new List<string>(), verbose: false);

        Assert.Equal(newId, result);
        Entity created = Assert.Single(svc.Created);
        Assert.Equal("pluginpackage", created.LogicalName);
        Assert.Equal("acme_Pkg", created.GetAttributeValue<string>("name"));
        Assert.Equal("2.3.4", created.GetAttributeValue<string>("version"));
        Assert.Equal(Convert.ToBase64String(bytes), created.GetAttributeValue<string>("content"));
        // Solution membership is applied by the solution-scoped service (SolutionUniqueName parameter), not a
        // solutionid attribute on the record.
        Assert.False(created.Contains("solutionid"));
        Assert.Empty(svc.Updated);
    }

    [Fact]
    public void UpsertPluginPackage_updates_existing_package_without_solutionid()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-bytes-2");
        string path = NewTempFile(bytes);
        var existingId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(new Entity("pluginpackage", existingId)),
        };

        var payload = new PluginPackagePayload("acme_Pkg", "9.9.9", path);
        Guid result = DataverseRegistration.UpsertPluginPackage(svc, payload, solutionPrefix: null, new List<string>(), verbose: false);

        Assert.Equal(existingId, result);
        Entity updated = Assert.Single(svc.Updated);
        Assert.Equal("pluginpackage", updated.LogicalName);
        Assert.Equal(existingId, updated.Id);
        // Dataverse derives the version from the uploaded .nupkg and rejects an explicit version write on an
        // existing package, so the update writes content only.
        Assert.False(updated.Contains("version"));
        Assert.Equal(Convert.ToBase64String(bytes), updated.GetAttributeValue<string>("content"));
        Assert.False(updated.Contains("solutionid"));
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void UpsertPluginPackage_skips_update_when_version_and_content_unchanged()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-bytes-same");
        string path = NewTempFile(bytes);
        var existingId = Guid.NewGuid();
        var existing = new Entity("pluginpackage", existingId);
        existing["version"] = "9.9.9";
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };
        svc.PackageFiles.Store(existing.Id, bytes); // the stored .nupkg (content reads back empty)

        var payload = new PluginPackagePayload("acme_Pkg", "9.9.9", path);
        Guid result = DataverseRegistration.UpsertPluginPackage(svc, payload, solutionPrefix: null, new List<string>(), verbose: false);

        Assert.Equal(existingId, result);
        // Unchanged content + version -> no Update (which would otherwise bump modifiedon on every apply).
        Assert.Empty(svc.Updated);
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void UpsertPluginPackage_skips_update_when_only_the_stored_version_is_stale()
    {
        // The stored version stays at 1.0.0 after 1.0.1 was uploaded (verified live, #492); the file is 1.0.1.
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-1.0.1");
        string path = NewTempFile(bytes);
        var existing = new Entity("pluginpackage", Guid.NewGuid()) { ["version"] = "1.0.0" };
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.Store(existing.Id, bytes);

        DataverseRegistration.UpsertPluginPackage(svc, new PluginPackagePayload("acme_Pkg", "1.0.1", path), solutionPrefix: null, new List<string>(), verbose: false);

        Assert.Empty(svc.Updated);
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void UpsertPluginPackage_explains_the_legacy_package_cap_when_Dataverse_rejects_a_package_above_net471()
    {
        var fault = new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Import failed.");
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => new EntityCollection(),
            OnCreate = _ => throw fault,
        };

        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", BuildPluginNupkg("net48"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.UpsertPluginPackage(svc, payload, solutionPrefix: null, new List<string>(), verbose: false));

        Assert.Contains("'acme_Pkg', which targets net48: Import failed.", ex.Message);
        Assert.Contains("register only on Dataverse 9.2.26092 or later", ex.Message);
        Assert.Contains("<TargetFramework>net471</TargetFramework>", ex.Message);
        Assert.Same(fault, ex.InnerException);
    }

    [Fact]
    public void UpsertPluginPackage_rethrows_a_rejection_as_is_when_the_package_is_within_the_legacy_cap()
    {
        var fault = new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Import failed.");
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => new EntityCollection(),
            OnCreate = _ => throw fault,
        };

        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", BuildPluginNupkg("net471"));
        var ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
            () => DataverseRegistration.UpsertPluginPackage(svc, payload, solutionPrefix: null, new List<string>(), verbose: false));

        Assert.Same(fault, ex);
    }

    [Fact]
    public void UpsertPluginPackage_updates_when_content_changed_even_if_version_matches()
    {
        byte[] oldBytes = Encoding.UTF8.GetBytes("pkg-old");
        byte[] newBytes = Encoding.UTF8.GetBytes("pkg-new");
        string path = NewTempFile(newBytes);
        var existingId = Guid.NewGuid();
        var existing = new Entity("pluginpackage", existingId);
        existing["version"] = "9.9.9";
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };
        svc.PackageFiles.Store(existing.Id, oldBytes); // the stored .nupkg (content reads back empty)

        var payload = new PluginPackagePayload("acme_Pkg", "9.9.9", path);
        DataverseRegistration.UpsertPluginPackage(svc, payload, solutionPrefix: null, new List<string>(), verbose: false);

        Entity updated = Assert.Single(svc.Updated);
        Assert.Equal(Convert.ToBase64String(newBytes), updated.GetAttributeValue<string>("content"));
    }

    [Fact]
    public void PluginPackagesNeedPublish_true_when_package_is_new()
    {
        string path = NewTempFile(Encoding.UTF8.GetBytes("pkg-new"));
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection() };
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", path);

        Assert.True(DataverseRegistration.PluginPackagesNeedPublish(svc, new[] { payload }, solutionUniqueName: null, new List<string>()));
    }

    [Fact]
    public void PluginPackagesNeedPublish_true_when_content_differs()
    {
        string path = NewTempFile(Encoding.UTF8.GetBytes("pkg-new"));
        var existing = new Entity("pluginpackage", Guid.NewGuid());
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.Store(existing.Id, Encoding.UTF8.GetBytes("pkg-old")); // the stored .nupkg (content reads back empty)
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", path);

        Assert.True(DataverseRegistration.PluginPackagesNeedPublish(svc, new[] { payload }, solutionUniqueName: null, new List<string>()));
    }

    [Fact]
    public void PluginPackagesNeedPublish_false_when_version_and_content_identical()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-same");
        string path = NewTempFile(bytes);
        var existing = new Entity("pluginpackage", Guid.NewGuid());
        existing["version"] = "1.0.0";
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.Store(existing.Id, bytes); // the stored .nupkg (content reads back empty)
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", path);

        Assert.False(DataverseRegistration.PluginPackagesNeedPublish(svc, new[] { payload }, solutionUniqueName: null, new List<string>()));
    }

    [Fact]
    public void PluginPackagesNeedPublish_false_when_only_the_stored_version_is_stale()
    {
        // A content update leaves the stored version at its first value (verified live, #492): after 1.0.0 -> 1.0.1
        // the record still says 1.0.0 while its file is the 1.0.1 package. Identical bytes are no change.
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-1.0.1");
        string path = NewTempFile(bytes);
        var existing = new Entity("pluginpackage", Guid.NewGuid());
        existing["version"] = "1.0.0";
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.Store(existing.Id, bytes); // the stored .nupkg (content reads back empty)
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.1", path);

        Assert.False(DataverseRegistration.PluginPackagesNeedPublish(svc, new[] { payload }, solutionUniqueName: null, new List<string>()));
    }

    [Fact]
    public void ComputePackagePublishActions_throws_when_the_package_file_is_missing()
    {
        // Matches UpsertPluginPackage so plan and apply agree: a missing package file is an error, not a clean op.
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection() };
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", Path.Combine(Path.GetTempPath(), $"vela-missing-{Guid.NewGuid():N}.nupkg"));

        Assert.Throws<FileNotFoundException>(() =>
            DataverseRegistration.ComputePackagePublishActions(svc, new[] { payload }, solutionUniqueName: null, new List<string>()));
    }

    [Fact]
    public void EnsurePluginPackagesImportable_throws_for_an_unprefixed_package_without_a_solution()
    {
        var payload = new PluginPackagePayload("Plugins", "1.0.0", "ignored");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseRegistration.EnsurePluginPackagesImportable(new[] { payload }, solutionUniqueName: null));
        Assert.Contains("no solution publisher prefix", ex.Message);
    }

    [Fact]
    public void EnsurePluginPackagesImportable_allows_a_prefixed_package_without_a_solution()
    {
        var payload = new PluginPackagePayload("acme_Plugins", "1.0.0", "ignored");

        // A name that already carries a plausible prefix can import on its own; no throw.
        DataverseRegistration.EnsurePluginPackagesImportable(new[] { payload }, solutionUniqueName: null);
    }

    [Fact]
    public void PluginPackagesNeedPublish_false_when_no_payloads()
    {
        Assert.False(DataverseRegistration.PluginPackagesNeedPublish(new TestSupport.FakeOrgService(), Array.Empty<PluginPackagePayload>(), solutionUniqueName: null, new List<string>()));
    }

    [Fact]
    public void ComputePackagePublishActions_create_when_package_is_new()
    {
        string path = NewTempFile(Encoding.UTF8.GetBytes("pkg-new"));
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection() };
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", path);

        PackagePublishAction action = Assert.Single(DataverseRegistration.ComputePackagePublishActions(svc, new[] { payload }, solutionUniqueName: null, new List<string>()));
        Assert.Equal("acme_Pkg", action.PackageName);
        Assert.True(action.IsCreate);
    }

    [Fact]
    public void ComputePackagePublishActions_update_when_content_differs()
    {
        string path = NewTempFile(Encoding.UTF8.GetBytes("pkg-new"));
        var existing = new Entity("pluginpackage", Guid.NewGuid());
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.Store(existing.Id, Encoding.UTF8.GetBytes("pkg-old")); // the stored .nupkg (content reads back empty)
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", path);

        PackagePublishAction action = Assert.Single(DataverseRegistration.ComputePackagePublishActions(svc, new[] { payload }, solutionUniqueName: null, new List<string>()));
        Assert.False(action.IsCreate);
    }

    [Fact]
    public void ComputePackagePublishActions_empty_when_only_the_stored_version_is_stale()
    {
        // Same bytes, stale stored version (a content update never moves it): nothing to publish.
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-1.0.1");
        string path = NewTempFile(bytes);
        var existing = new Entity("pluginpackage", Guid.NewGuid()) { ["version"] = "1.0.0" };
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.Store(existing.Id, bytes); // the stored .nupkg (content reads back empty)

        Assert.Empty(DataverseRegistration.ComputePackagePublishActions(svc, new[] { new PluginPackagePayload("acme_Pkg", "1.0.1", path) }, solutionUniqueName: null, new List<string>()));
    }

    [Fact]
    public void RepackWithPackageId_keeps_every_other_entry_name_and_content()
    {
        // Only the .nuspec changes: the assembly and any other entry keep their path and bytes.
        string nuspec = "<package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\"><metadata><id>Old.Name</id><version>1.0.0</version></metadata></package>";
        string nupkg = BuildZip(new Dictionary<string, string> { ["Old.Name.nuspec"] = nuspec, ["readme.txt"] = "payload-bytes" });

        byte[] result = DataverseRegistration.ReadPackageBytesForImport(nupkg, "Old.Name", "new_Old.Name");

        using ZipArchive repacked = new ZipArchive(new MemoryStream(result));
        Assert.Equal(new[] { "new_Old.Name.nuspec", "readme.txt" }, repacked.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        using StreamReader reader = new StreamReader(repacked.GetEntry("readme.txt")!.Open());
        Assert.Equal("payload-bytes", reader.ReadToEnd());
    }

    [Fact]
    public void ComputePackagePublishActions_empty_when_version_and_content_identical()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-same");
        string path = NewTempFile(bytes);
        var existing = new Entity("pluginpackage", Guid.NewGuid());
        existing["version"] = "1.0.0";
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.Store(existing.Id, bytes); // the stored .nupkg (content reads back empty)
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", path);

        Assert.Empty(DataverseRegistration.ComputePackagePublishActions(svc, new[] { payload }, solutionUniqueName: null, new List<string>()));
    }

    [Fact]   // #492
    public void ComputePackagePublishActions_update_when_the_stored_package_has_no_file()
    {
        // No stored file ("No FileAttachment records found"): nothing to compare, so the package is published.
        string path = NewTempFile(Encoding.UTF8.GetBytes("pkg-same"));
        var existing = new Entity("pluginpackage", Guid.NewGuid()) { ["version"] = "1.0.0" };
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };

        PackagePublishAction action = Assert.Single(DataverseRegistration.ComputePackagePublishActions(svc, new[] { new PluginPackagePayload("acme_Pkg", "1.0.0", path) }, solutionUniqueName: null, new List<string>()));
        Assert.False(action.IsCreate);
    }

    [Fact]   // #492
    public void ComputePackagePublishActions_tells_a_resized_package_apart_without_downloading_it()
    {
        string path = NewTempFile(Encoding.UTF8.GetBytes("pkg-longer"));
        var existing = new Entity("pluginpackage", Guid.NewGuid()) { ["version"] = "1.0.0" };
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.Store(existing.Id, Encoding.UTF8.GetBytes("pkg-old"));

        Assert.Single(DataverseRegistration.ComputePackagePublishActions(svc, new[] { new PluginPackagePayload("acme_Pkg", "1.0.0", path) }, solutionUniqueName: null, new List<string>()));
        Assert.Empty(svc.PackageFiles.BlockDownloads);
    }

    [Theory]   // #492
    [InlineData(false)]
    [InlineData(true)]
    public void ComputePackagePublishActions_compares_a_large_stored_package_block_by_block(bool lastByteDiffers)
    {
        // A package larger than one 4 MB block is downloaded in blocks; a difference in the last block is still seen.
        byte[] local = new byte[(9 * 1024 * 1024) + 123];
        new Random(445).NextBytes(local);
        byte[] stored = (byte[])local.Clone();
        if (lastByteDiffers)
        {
            stored[^1] ^= 0xFF;
        }

        string path = NewTempFile(local);
        var existing = new Entity("pluginpackage", Guid.NewGuid()) { ["version"] = "1.0.0" };
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.Store(existing.Id, stored);

        IReadOnlyList<PackagePublishAction> actions = DataverseRegistration.ComputePackagePublishActions(svc, new[] { new PluginPackagePayload("acme_Pkg", "1.0.0", path) }, solutionUniqueName: null, new List<string>());

        Assert.Equal(lastByteDiffers, actions.Count == 1);
        Assert.Equal(new long[] { 0, 4L * 1024 * 1024, 8L * 1024 * 1024 }, svc.PackageFiles.BlockDownloads.Select(b => b.Offset));
    }

    [Theory]   // PR #494 review
    [InlineData(false)]
    [InlineData(true)]
    public void ComputePackagePublishActions_downloads_the_whole_file_in_one_block_when_chunking_is_not_supported(bool lastByteDiffers)
    {
        // A non-chunked download must request the whole file in one block: a 4 MB block would be refused, and an
        // unchanged package over 4 MB would be republished on every run.
        byte[] local = new byte[(5 * 1024 * 1024) + 7];
        new Random(494).NextBytes(local);
        byte[] stored = (byte[])local.Clone();
        if (lastByteDiffers)
        {
            stored[^1] ^= 0xFF;
        }

        string path = NewTempFile(local);
        var existing = new Entity("pluginpackage", Guid.NewGuid()) { ["version"] = "1.0.0" };
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        svc.PackageFiles.ChunkingSupported = false;
        svc.PackageFiles.Store(existing.Id, stored);

        IReadOnlyList<PackagePublishAction> actions = DataverseRegistration.ComputePackagePublishActions(svc, new[] { new PluginPackagePayload("acme_Pkg", "1.0.0", path) }, solutionUniqueName: null, new List<string>());

        Assert.Equal(lastByteDiffers, actions.Count == 1);
        Assert.Equal([(0L, local.LongLength)], svc.PackageFiles.BlockDownloads);
    }

    [Fact]
    public void UpsertPluginPackage_throws_when_package_file_missing()
    {
        var svc = new TestSupport.FakeOrgService();
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".nupkg"));
        var ex = Assert.Throws<FileNotFoundException>(
            () => DataverseRegistration.UpsertPluginPackage(svc, payload, null, new List<string>(), verbose: false));
        Assert.Contains("Plugin package file not found", ex.Message);
    }

    // ---- EnsurePluginPackages -------------------------------------------------------------------

    [Fact]
    public void EnsurePluginPackages_without_solution_upserts_each_package()
    {
        string path = NewTempFile(Encoding.UTF8.GetBytes("p"));
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => new EntityCollection(), // no existing package
        };

        DataverseRegistration.EnsurePluginPackages(
            svc, [new PluginPackagePayload("acme_Pkg", "1.0.0", path)], solutionUniqueName: null, new List<string>(), verbose: false);

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("pluginpackage", created.LogicalName);
        Assert.Equal("acme_Pkg", created.GetAttributeValue<string>("name")); // already prefixed → imports as-is
        // No solution → no solution/publisher queries issued.
        Assert.DoesNotContain(svc.Queries, q => q.EntityName == "solution");
    }

    [Fact]
    public void EnsurePluginPackages_without_packages_does_not_resolve_the_solution_prefix()
    {
        // A run that only fixes solution membership (or deploys web resources) has no package: no solution lookup,
        // so an unknown/irrelevant solution cannot fail the publish phase.
        var svc = new TestSupport.FakeOrgService();

        DataverseRegistration.EnsurePluginPackages(svc, [], "acme_solution", new List<string>(), verbose: false);

        Assert.Empty(svc.Queries);
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void EnsurePluginPackages_without_solution_and_unprefixed_name_throws()
    {
        // An unprefixed package (e.g. the project name "Zava.Plugins") can't import without a --solution to source
        // the publisher prefix — fail fast with a clear message instead of the platform's cryptic 0x80040265.
        var svc = new TestSupport.FakeOrgService();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseRegistration.EnsurePluginPackages(
                svc, [new PluginPackagePayload("Zava.Plugins", "1.0.0", "unused.nupkg")], solutionUniqueName: null, new List<string>(), verbose: false));

        Assert.Contains("solution publisher prefix", ex.Message);
        Assert.Contains("--solution", ex.Message);
        Assert.Empty(svc.Created); // failed fast, nothing sent to Dataverse
    }

    [Fact]
    public void EnsurePluginPackages_with_solution_resolves_prefix_and_id()
    {
        // A prefix is applied, changing the package name → ReadPackageBytesForImport re-zips, so the
        // payload must be a real .nupkg with a nuspec.
        string path = BuildNupkg("Pkg", nuspecId: "Pkg");
        var publisherId = Guid.NewGuid();
        var solutionId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "solution"
                ? TestSupport.FakeOrgService.Collection(new Entity("solution", solutionId) { ["publisherid"] = new EntityReference("publisher", publisherId) })
                : new EntityCollection(),
            OnRetrieve = (entity, id, _) => new Entity(entity, id) { ["customizationprefix"] = "new" },
        };

        DataverseRegistration.EnsurePluginPackages(
            svc, [new PluginPackagePayload("Pkg", "1.0.0", path)], solutionUniqueName: "acme_solution", new List<string>(), verbose: false);

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("new_Pkg", created.GetAttributeValue<string>("name")); // prefix applied
        // Solution membership is applied by the solution-scoped service (SolutionUniqueName parameter), not a
        // solutionid attribute.
        Assert.False(created.Contains("solutionid"));
    }

    [Fact]
    public void ApplyDiff_with_a_solution_publishes_the_package_into_that_solution()
    {
        // ApplyDiff routes package/assembly writes through the solution-scoped service, so the import lands in the
        // solution (SolutionUniqueName on the request) instead of only the default solution.
        string path = BuildNupkg("Pkg", nuspecId: "Pkg");
        var publisherId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "solution"
                ? TestSupport.FakeOrgService.Collection(new Entity("solution", Guid.NewGuid()) { ["publisherid"] = new EntityReference("publisher", publisherId) })
                : new EntityCollection(),
            OnRetrieve = (entity, id, _) => new Entity(entity, id) { ["customizationprefix"] = "new" },
        };
        var diff = new RegistrationDiff(["Pkg"], ["Pkg"], [], [], [], [], []);

        DataverseRegistration.ApplyDiff(svc, diff, [new PluginPackagePayload("Pkg", "1.0.0", path)], "acme_solution", new List<string>(), verbose: false);

        CreateRequest create = Assert.Single(svc.Executed.OfType<CreateRequest>(), r => r.Target.LogicalName == "pluginpackage");
        Assert.Equal("acme_solution", create["SolutionUniqueName"]);
    }

    // ---- WaitForRequiredPluginTypes (success path) -------------------------

    [Fact]
    public void WaitForRequiredPluginTypes_returns_immediately_when_types_present()
    {
        var assemblyId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginassembly"
                ? TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", assemblyId))
                : TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Acme.Plugins.AccountPlugin" }),
        };

        DataverseRegistration.WaitForRequiredPluginTypes(svc, ["Acme.Plugins"], ["Acme.Plugins.AccountPlugin"]);
    }

    // ---- ApplyDiff: missing plugin type after publish -------------------------------------------

    [Fact]
    public void ApplyDiff_throws_when_required_plugin_type_never_resolves()
    {
        // Two steps requiring plugin types that no package provides. With no packages there is nothing to wait for
        // and no direct assembly upload to fall back on, so apply must fail listing every missing
        // type in ordinal order.
        var svc = new TestSupport.FakeOrgService(); // every query returns empty
        JsonElement defA = Json(new { pluginTypeName = "Ghost.Plugins.Missing", message = "Update", stage = 40, mode = 0, stepKey = "dv-a" });
        JsonElement defB = Json(new { pluginTypeName = "Ghost.Plugins.AnotherMissing", message = "Update", stage = 40, mode = 0, stepKey = "dv-b" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Ghost.Plugins"],
            CurrentAssemblies: ["Ghost.Plugins"],
            Added: [new RegistrationItem("step", "Ghost.Plugins.Missing", defA), new RegistrationItem("step", "Ghost.Plugins.AnotherMissing", defB)],
            Removed: [],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            var ex = Assert.ThrowsAny<Exception>(
                () => DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false));
            Assert.Contains("Plugin type not found in Dataverse after the plugin package publish", ex.Message);
            // Ordinal sort: "AnotherMissing" precedes "Missing".
            int a = ex.Message.IndexOf("Ghost.Plugins.AnotherMissing", StringComparison.Ordinal);
            int b = ex.Message.IndexOf("Ghost.Plugins.Missing", StringComparison.Ordinal);
            Assert.True(a >= 0 && b >= 0 && a < b, "both missing types listed in ordinal order");
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void ApplyDiff_midrun_failure_reports_exact_completed_and_remaining_split()
    {
        // A rich diff — one removed step, two added steps, one changed step — that fails on the first added
        // step (its sdkmessage lookup). The resume report must label every planned operation and split the
        // completed/pending lists at exactly the failure boundary (delete + publish done, creates/update not).
        const string plugin = "Zava.Plugins.AccountPlugin";
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessageprocessingstep" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessageprocessingstep", Guid.NewGuid())), // removed step found → deleted
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = plugin }),
                "sdkmessage" => throw new InvalidOperationException("boom-at-first-added-step"),
                _ => new EntityCollection(),
            },
        };

        JsonElement removed = Json(new { pluginTypeName = plugin, message = "Update", stage = 40, mode = 0, stepKey = "dv-old", name = "OldStep" });
        JsonElement add1 = Json(new { pluginTypeName = plugin, message = "Update", stage = 40, mode = 0, stepKey = "dv-1" });
        JsonElement add2 = Json(new { pluginTypeName = plugin, message = "Update", stage = 40, mode = 0, stepKey = "dv-2" });
        JsonElement changed = Json(new { pluginTypeName = plugin, message = "Update", stage = 40, mode = 0, stepKey = "dv-3" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("step", plugin, add1), new RegistrationItem("step", plugin, add2)],
            Removed: [new RegistrationItem("step", plugin, removed)],
            Changed: [new RegistrationChange("step", plugin, changed, changed)],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains($"failed at: create step {plugin}", stderr);
        Assert.Contains("boom-at-first-added-step", stderr);
        // Completed = removed-step delete + publish/resolve (in order); pending = the two creates + the update.
        Assert.Contains("completed (2):", stderr);
        Assert.Contains("+ delete step dv-old", stderr);
        Assert.Contains("+ publish plugin package(s) and resolve plugin types", stderr);
        Assert.Contains("not applied (2):", stderr);
        Assert.Contains($"- update step {plugin}", stderr);
    }

    // ---- ApplyDiff: Custom API orchestration ----------------------------------------------------

    [Fact]
    public void ApplyDiff_deletes_adds_and_updates_custom_apis()
    {
        const string apiType = "Zava.Plugins.MyApi";
        var apiTypeId = Guid.NewGuid();
        var removedApiId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            // The removed API is still implemented by our type when it is deleted.
            OnRetrieve = (e, id, _) => e == "customapi" ? new Entity(e, id) { ["plugintypeid"] = new EntityReference("plugintype", apiTypeId) } : new Entity(e, id),
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", apiTypeId) { ["typename"] = apiType }),
                // The removed API exists (→ delete); the added/changed ones don't (→ create).
                "customapi" => (string)q.Criteria.Conditions.Single().Values.Single() == "zava_Removed"
                    ? TestSupport.FakeOrgService.Collection(new Entity("customapi", removedApiId))
                    : new EntityCollection(),
                _ => new EntityCollection(),
            },
        };

        JsonElement removed = Json(new { pluginTypeName = apiType, uniqueName = "zava_Removed" });
        JsonElement added = Json(new { pluginTypeName = apiType, uniqueName = "zava_Added" });
        JsonElement changed = Json(new { pluginTypeName = apiType, uniqueName = "zava_Changed" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("custom-api", apiType, added)],
            Removed: [new RegistrationItem("custom-api", apiType, removed)],
            Changed: [new RegistrationChange("custom-api", apiType, changed, changed)],
            LocalItems: [],
            CurrentItems: []);

        DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false);

        // Removed API deleted through the seam.
        Assert.Contains(("customapi", removedApiId), svc.Deleted);
        // Added + changed APIs created (upsert with no existing row → create).
        Assert.Contains(svc.Created, e => e.LogicalName == "customapi" && e.GetAttributeValue<string>("uniquename") == "zava_Added");
        Assert.Contains(svc.Created, e => e.LogicalName == "customapi" && e.GetAttributeValue<string>("uniquename") == "zava_Changed");
    }

    [Fact]
    public void ApplyDiff_resume_report_labels_custom_api_operations()
    {
        const string apiType = "Zava.Plugins.MyApi";
        var apiTypeId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            // The removed API is still implemented by our type when it is deleted.
            OnRetrieve = (e, id, _) => e == "customapi" ? new Entity(e, id) { ["plugintypeid"] = new EntityReference("plugintype", apiTypeId) } : new Entity(e, id),
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", apiTypeId) { ["typename"] = apiType }),
                "customapi" => (string)q.Criteria.Conditions.Single().Values.Single() == "zava_Removed"
                    ? TestSupport.FakeOrgService.Collection(new Entity("customapi", Guid.NewGuid()))
                    : new EntityCollection(),
                _ => new EntityCollection(),
            },
            OnCreate = e => e.LogicalName == "customapi" ? throw new InvalidOperationException("boom-creating-api") : Guid.NewGuid(),
        };

        JsonElement removed = Json(new { pluginTypeName = apiType, uniqueName = "zava_Removed" });
        JsonElement added = Json(new { pluginTypeName = apiType, uniqueName = "zava_Added" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("custom-api", apiType, added)],
            Removed: [new RegistrationItem("custom-api", apiType, removed)],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains($"failed at: create custom-api {apiType}", stderr);
        // The removed-API delete and the publish/resolve completed before the failure.
        Assert.Contains("+ delete custom-api zava_Removed", stderr);
        Assert.Contains("+ publish plugin package(s) and resolve plugin types", stderr);
    }

    [Fact]
    public void ApplyDiff_skips_step_delete_on_ignorable_stage_fault_and_continues()
    {
        // Deleting a step that the platform manages (custom-API main operation) raises an "Invalid plug-in
        // registration stage" fault. Apply must warn and carry on, not abort the whole run.
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessageprocessingstep" => throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Invalid plug-in registration stage."),
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Zava.Plugins.AccountPlugin" }),
                _ => new EntityCollection(),
            },
        };
        JsonElement removed = Json(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Update", stage = 40, mode = 0, stepKey = "dv-x", name = "ManagedStep" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [],
            Removed: [new RegistrationItem("step", "Zava.Plugins.AccountPlugin", removed)],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false); // must NOT throw
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("skipping step delete", captured.ToString());
    }

    [Fact]
    public void ApplyDiff_skips_custom_api_delete_on_ignorable_stage_fault_and_continues()
    {
        const string apiType = "Zava.Plugins.MyApi";
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "customapi"
                ? throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Invalid plug-in registration stage.")
                : new EntityCollection(),
        };
        JsonElement removed = Json(new { pluginTypeName = apiType, uniqueName = "zava_Managed" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [],
            Removed: [new RegistrationItem("custom-api", apiType, removed)],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false); // must NOT throw
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("skipping custom API delete", captured.ToString());
    }

    [Fact]
    public void ApplyDiff_midrun_capture_abort_does_not_report_unflushed_creates_as_completed()   // #305
    {
        // Removed step (deletes in its own batch, before publish) + two added steps + one changed step whose message
        // does not resolve. The changed step throws during the upsert CAPTURE phase, before the batch is flushed - so
        // the two creates never landed either. The resume report must therefore count only the delete + publish as
        // completed (not the creates), and name the step that threw as the failure. Before the fix, done advanced during
        // capture and the creates were falsely reported as applied.
        const string plugin = "Zava.Plugins.AccountPlugin";
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                // Removed-step delete looks up by name; added/changed FindStepId filters by eventhandler.
                "sdkmessageprocessingstep" => q.Criteria.Conditions.Any(c => c.AttributeName == "name")
                    ? TestSupport.FakeOrgService.Collection(new Entity("sdkmessageprocessingstep", Guid.NewGuid()))
                    : new EntityCollection(),
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = plugin }),
                // "Update" resolves; the changed step's "Delete" message does not → ResolveMessageId throws.
                "sdkmessage" => (string)q.Criteria.Conditions.Single().Values.Single() == "Update"
                    ? TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid()))
                    : new EntityCollection(),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id),
        };

        JsonElement removed = Json(new { pluginTypeName = plugin, message = "Update", entityLogicalName = "", stage = 40, mode = 0, stepKey = "dv-old", name = "OldStep" });
        JsonElement add1 = Json(new { pluginTypeName = plugin, message = "Update", entityLogicalName = "", stage = 40, mode = 0, stepKey = "dv-1", enabled = true });
        JsonElement add2 = Json(new { pluginTypeName = plugin, message = "Update", entityLogicalName = "", stage = 40, mode = 0, stepKey = "dv-2", enabled = true });
        JsonElement changed = Json(new { pluginTypeName = plugin, message = "Delete", entityLogicalName = "", stage = 40, mode = 0, stepKey = "dv-3", enabled = true });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("step", plugin, add1), new RegistrationItem("step", plugin, add2)],
            Removed: [new RegistrationItem("step", plugin, removed)],
            Changed: [new RegistrationChange("step", plugin, changed, changed)],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        // Only the delete (its own batch) + publish completed; the whole upsert batch was un-flushed.
        Assert.Contains("completed (2):", stderr);
        Assert.Contains("+ delete step dv-old", stderr);
        Assert.Contains("+ publish plugin package(s) and resolve plugin types", stderr);
        Assert.DoesNotContain($"+ create step {plugin}", stderr);   // creates never flushed -> not completed
        Assert.Contains($"failed at: update step {plugin} (dv-3)", stderr); // the owner whose capture threw
        Assert.Contains($"- create step {plugin} (dv-1)", stderr);         // captured, never flushed
        Assert.Contains($"- create step {plugin} (dv-2)", stderr);
    }

    [Fact]
    public void ApplyDiff_publishes_package_waits_for_types_then_creates_step()
    {
        // The package branch: a plugin package is published, the required plugin type is initially absent (so
        // apply waits), then it materializes and the step is created. Exercises EnsurePluginPackages + the
        // packagePayloads-present wait path + the step upsert end-to-end.
        const string plugin = "Zava.Plugins.AccountPlugin";
        string nupkg = NewTempFile(Encoding.UTF8.GetBytes("pkg-bytes"));
        int pluginTypeCalls = 0;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                switch (q.EntityName)
                {
                    case "pluginpackage": return new EntityCollection();            // no existing package → create
                    case "pluginassembly": return TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()));
                    case "plugintype":
                        pluginTypeCalls++;
                        return pluginTypeCalls == 1
                            ? new EntityCollection()                                 // absent initially → drives the wait
                            : TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = plugin });
                    case "sdkmessage": return TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid()));
                    default: return new EntityCollection();                          // FindStepId + images empty
                }
            },
            OnRetrieve = (e, id, _) => new Entity(e, id),                            // secure-config reconcile: none
        };

        JsonElement step = Json(new { pluginTypeName = plugin, message = "Update", entityLogicalName = "", stage = 40, mode = 0, stepKey = "dv-1", enabled = true });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("step", plugin, step)],
            Removed: [],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);
        // Already-prefixed package name, so no --solution is needed to import it (the prefixing path is covered by
        // EnsurePluginPackages_with_solution_resolves_prefix_and_id).
        var packages = new[] { new PluginPackagePayload("acme_ZavaPlugins", "1.0.0", nupkg) };

        DataverseRegistration.ApplyDiff(svc, diff, packages, solutionUniqueName: null, new List<string>(), verbose: false);

        // The package was published with its name/version/content...
        Entity pkg = svc.Created.Single(e => e.LogicalName == "pluginpackage");
        Assert.Equal("acme_ZavaPlugins", pkg.GetAttributeValue<string>("name"));
        Assert.Equal("1.0.0", pkg.GetAttributeValue<string>("version"));
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("pkg-bytes")), pkg.GetAttributeValue<string>("content"));
        // ...and, once the type resolved, the step was created bound to it.
        Entity createdStep = svc.Created.Single(e => e.LogicalName == "sdkmessageprocessingstep");
        Assert.NotEqual(Guid.Empty, createdStep.GetAttributeValue<EntityReference>("eventhandler").Id);
        Assert.Contains("dv-1", createdStep.GetAttributeValue<string>("name"));
    }

    [Fact]
    public void ApplyDiff_capture_abort_does_not_report_unflushed_custom_api_create_as_completed()   // #305
    {
        // removed api (deletes in its own batch) + added api + changed api whose lookup throws during the upsert
        // capture phase. The added api's create is captured but never flushed (the batch aborts), so only the delete +
        // publish completed; the changed api (whose lookup threw) is the failure and the create is "not applied".
        const string apiType = "Zava.Plugins.MyApi";
        var apiTypeId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            // The removed API is still implemented by our type when it is deleted.
            OnRetrieve = (e, id, _) => e == "customapi" ? new Entity(e, id) { ["plugintypeid"] = new EntityReference("plugintype", apiTypeId) } : new Entity(e, id),
            OnRetrieveMultiple = q =>
            {
                switch (q.EntityName)
                {
                    case "pluginassembly": return TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()));
                    case "plugintype": return TestSupport.FakeOrgService.Collection(new Entity("plugintype", apiTypeId) { ["typename"] = apiType });
                    case "customapi":
                        string uname = (string)q.Criteria.Conditions.Single().Values.Single();
                        if (uname == "zava_Removed") return TestSupport.FakeOrgService.Collection(new Entity("customapi", Guid.NewGuid()));
                        if (uname == "zava_Changed") throw new InvalidOperationException("boom-on-changed-api-lookup");
                        return new EntityCollection(); // zava_Added → create
                    default: return new EntityCollection();
                }
            },
        };

        JsonElement removed = Json(new { pluginTypeName = apiType, uniqueName = "zava_Removed" });
        JsonElement added = Json(new { pluginTypeName = apiType, uniqueName = "zava_Added" });
        JsonElement changed = Json(new { pluginTypeName = apiType, uniqueName = "zava_Changed" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("custom-api", apiType, added)],
            Removed: [new RegistrationItem("custom-api", apiType, removed)],
            Changed: [new RegistrationChange("custom-api", apiType, changed, changed)],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("completed (2):", stderr);                    // delete + publish only
        Assert.DoesNotContain($"+ create custom-api {apiType}", stderr);  // captured but never flushed
        Assert.Contains($"failed at: update custom-api {apiType}", stderr); // the owner whose capture threw
        Assert.Contains($"- create custom-api {apiType}", stderr);         // captured, never flushed
    }

    [Fact]
    public void ApplyDiff_capture_abort_does_not_report_unflushed_custom_api_update_as_completed()   // #305
    {
        // A changed custom-api is captured, then an added step throws during the same upsert capture phase. Nothing in
        // the batch flushed, so only the publish completed; the api update is captured-but-unflushed ("not applied")
        // and the step whose capture threw is the failure.
        const string type = "Zava.Plugins.Thing";
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = type }),
                "customapi" => TestSupport.FakeOrgService.Collection(new Entity("customapi", Guid.NewGuid())), // exists → update path
                "sdkmessage" => throw new InvalidOperationException("boom-at-added-step"),
                _ => new EntityCollection(),
            },
        };

        JsonElement changedApi = Json(new { pluginTypeName = type, uniqueName = "zava_Changed" });
        JsonElement addedStep = Json(new { pluginTypeName = type, message = "Update", entityLogicalName = "", stage = 40, mode = 0, stepKey = "dv-1", enabled = true });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("step", type, addedStep)],
            Removed: [],
            Changed: [new RegistrationChange("custom-api", type, changedApi, changedApi)],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("completed (1):", stderr);                 // publish only
        Assert.DoesNotContain($"+ update custom-api {type}", stderr);  // captured but never flushed
        Assert.Contains($"failed at: create step {type} (dv-1)", stderr); // the owner whose capture threw
        Assert.Contains($"- update custom-api {type}", stderr);          // captured, never flushed
    }

    [Fact]
    public void ApplyDiff_skips_custom_api_upsert_and_update_on_ignorable_stage_fault()
    {
        const string apiType = "Zava.Plugins.MyApi";
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = apiType }),
                _ => new EntityCollection(),   // customapi lookup empty → create path
            },
            // The customapi create raises the platform's ignorable stage fault.
            OnCreate = e => e.LogicalName == "customapi"
                ? throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Invalid plug-in registration stage.")
                : Guid.NewGuid(),
        };

        JsonElement added = Json(new { pluginTypeName = apiType, uniqueName = "zava_Added" });
        JsonElement changed = Json(new { pluginTypeName = apiType, uniqueName = "zava_Changed" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("custom-api", apiType, added)],
            Removed: [],
            Changed: [new RegistrationChange("custom-api", apiType, changed, changed)],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false); // must NOT throw
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("skipping custom API upsert", stderr);   // added path
        Assert.Contains("skipping custom API update", stderr);   // changed path
    }

    // ---- fixtures --------------------------------------------------------------------------------

    private static void XDocumentAssertId(string nuspecPath, string expectedId)
    {
        XDocument doc = XDocument.Load(nuspecPath);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        string? id = doc.Root?.Element(ns + "metadata")?.Element(ns + "id")?.Value;
        Assert.Equal(expectedId, id);
    }

    private string BuildNupkg(string packageName, string nuspecId)
    {
        string nuspec = $"<package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\"><metadata><id>{nuspecId}</id><version>1.0.0</version></metadata></package>";
        return BuildZip(new Dictionary<string, string> { [$"{packageName}.nuspec"] = nuspec });
    }

    private string BuildPluginNupkg(string tfm)
    {
        string zipPath = Path.Combine(Path.GetTempPath(), "vela-apply-test-" + Guid.NewGuid().ToString("N") + ".nupkg");
        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (Stream entry = zip.CreateEntry($"lib/{tfm}/Acme.Plugins.dll").Open())
        {
            entry.Write(Encoding.UTF8.GetBytes("dll"));
        }

        _tempPaths.Add(zipPath);
        return zipPath;
    }

    private string BuildZip(IReadOnlyDictionary<string, string> entries)
    {
        string dir = NewTempDir();
        foreach ((string name, string content) in entries)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }

        string zipPath = Path.Combine(Path.GetTempPath(), "vela-apply-test-" + Guid.NewGuid().ToString("N") + ".nupkg");
        ZipFile.CreateFromDirectory(dir, zipPath);
        _tempPaths.Add(zipPath);
        return zipPath;
    }
}
