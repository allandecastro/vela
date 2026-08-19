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

    // ---- ResolveSolutionId ----------------------------------------------------------------------

    [Fact]
    public void ResolveSolutionId_returns_id_and_queries_by_uniquename()
    {
        var solutionId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(new Entity("solution", solutionId)),
        };

        Assert.Equal(solutionId, DataverseRegistration.ResolveSolutionId(svc, "acme_solution"));

        QueryExpression q = Assert.Single(svc.Queries);
        Assert.Equal("solution", q.EntityName);
        Assert.Contains("solutionid", q.ColumnSet.Columns);
        ConditionExpression c = Assert.Single(q.Criteria.Conditions);
        Assert.Equal("uniquename", c.AttributeName);
        Assert.Equal("acme_solution", c.Values.Single());
    }

    [Fact]
    public void ResolveSolutionId_throws_when_solution_absent()
    {
        var svc = new TestSupport.FakeOrgService();
        var ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.ResolveSolutionId(svc, "nope"));
        Assert.Contains("Solution not found: nope", ex.Message);
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
        var tempDirs = new List<string>();

        byte[] result = DataverseRegistration.ReadPackageBytesForImport(path, "Same.Name", "Same.Name", tempDirs);

        Assert.Equal(bytes, result);
        Assert.Empty(tempDirs); // no rename → no temp extraction
    }

    [Fact]
    public void ReadPackageBytesForImport_rewrites_nuspec_id_when_renamed()
    {
        string nupkg = BuildNupkg("Old.Name", nuspecId: "Old.Name");
        var tempDirs = new List<string>();

        byte[] result = DataverseRegistration.ReadPackageBytesForImport(nupkg, "Old.Name", "new_Old.Name", tempDirs);

        Assert.Equal(2, tempDirs.Count); // both the extraction dir and the re-zip output dir are tracked for cleanup
        // The returned package must carry the renamed nuspec id, in a file named after the new id.
        string extractDir = NewTempDir();
        using var stream = new MemoryStream(result);
        ZipFile.ExtractToDirectory(stream, extractDir);
        string[] nuspecs = Directory.GetFiles(extractDir, "*.nuspec", SearchOption.TopDirectoryOnly);
        Assert.Equal("new_Old.Name.nuspec", Path.GetFileName(Assert.Single(nuspecs)));
        XDocumentAssertId(nuspecs[0], "new_Old.Name");
    }

    [Fact]
    public void ReadPackageBytesForImport_throws_when_no_nuspec()
    {
        string nupkg = BuildZip(new Dictionary<string, string> { ["readme.txt"] = "hi" });
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.ReadPackageBytesForImport(nupkg, "A", "b_A", new List<string>()));
        Assert.Contains("No .nuspec found", ex.Message);
    }

    [Fact]
    public void ReadPackageBytesForImport_throws_when_nuspec_missing_id()
    {
        string nuspec = "<package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\"><metadata><version>1.0.0</version></metadata></package>";
        string nupkg = BuildZip(new Dictionary<string, string> { ["pkg.nuspec"] = nuspec });
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.ReadPackageBytesForImport(nupkg, "A", "b_A", new List<string>()));
        Assert.Contains("missing metadata id", ex.Message);
    }

    // ---- UpsertPluginPackage --------------------------------------------------------------------

    [Fact]
    public void UpsertPluginPackage_creates_new_package_with_all_columns()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-bytes");
        string path = NewTempFile(bytes);
        var newId = Guid.NewGuid();
        var solutionId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => new EntityCollection(), // no existing package → create
            OnCreate = _ => newId,
        };

        var payload = new PluginPackagePayload("acme_Pkg", "2.3.4", path);
        Guid result = DataverseRegistration.UpsertPluginPackage(svc, payload, solutionPrefix: null, solutionId, new List<string>(), verbose: false);

        Assert.Equal(newId, result);
        Entity created = Assert.Single(svc.Created);
        Assert.Equal("pluginpackage", created.LogicalName);
        Assert.Equal("acme_Pkg", created.GetAttributeValue<string>("name"));
        Assert.Equal("2.3.4", created.GetAttributeValue<string>("version"));
        Assert.Equal(Convert.ToBase64String(bytes), created.GetAttributeValue<string>("content"));
        EntityReference solutionRef = created.GetAttributeValue<EntityReference>("solutionid");
        Assert.Equal("solution", solutionRef.LogicalName);
        Assert.Equal(solutionId, solutionRef.Id);
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
        Guid result = DataverseRegistration.UpsertPluginPackage(svc, payload, solutionPrefix: null, solutionId: null, new List<string>(), verbose: false);

        Assert.Equal(existingId, result);
        Entity updated = Assert.Single(svc.Updated);
        Assert.Equal("pluginpackage", updated.LogicalName);
        Assert.Equal(existingId, updated.Id);
        Assert.Equal("9.9.9", updated.GetAttributeValue<string>("version"));
        Assert.Equal(Convert.ToBase64String(bytes), updated.GetAttributeValue<string>("content"));
        Assert.False(updated.Contains("solutionid"));
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void UpsertPluginPackage_throws_when_package_file_missing()
    {
        var svc = new TestSupport.FakeOrgService();
        var payload = new PluginPackagePayload("acme_Pkg", "1.0.0", Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".nupkg"));
        var ex = Assert.Throws<FileNotFoundException>(
            () => DataverseRegistration.UpsertPluginPackage(svc, payload, null, null, new List<string>(), verbose: false));
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
        Assert.Equal(solutionId, created.GetAttributeValue<EntityReference>("solutionid").Id);
    }

    // ---- UpsertPluginAssembly -------------------------------------------------------------------

    [Fact]
    public void UpsertPluginAssembly_creates_unsigned_assembly_with_isolation_and_sourcetype()
    {
        string dll = typeof(DataverseRegistration).Assembly.Location; // unsigned CLI assembly
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => new EntityCollection(), // no existing → create
        };

        DataverseRegistration.UpsertPluginAssembly(svc, new AssemblyPayload("Vela.Cli", dll));

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("pluginassembly", created.LogicalName);
        Assert.Equal("Vela.Cli", created.GetAttributeValue<string>("name"));
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(dll)), created.GetAttributeValue<string>("content"));
        Assert.Equal(ExpectedVersion(dll), created.GetAttributeValue<string>("version"));
        Assert.Equal("neutral", created.GetAttributeValue<string>("culture"));
        Assert.Equal("null", created.GetAttributeValue<string>("publickeytoken")); // unsigned
        Assert.Equal(0, created.GetAttributeValue<OptionSetValue>("sourcetype").Value);
        Assert.Equal(2, created.GetAttributeValue<OptionSetValue>("isolationmode").Value);
    }

    [Fact]
    public void UpsertPluginAssembly_updates_existing_and_emits_hex_token_for_signed_assembly()
    {
        string dll = typeof(System.Xml.Linq.XDocument).Assembly.Location; // signed framework assembly
        var existingId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", existingId)),
        };

        DataverseRegistration.UpsertPluginAssembly(svc, new AssemblyPayload("System.Private.Xml.Linq", dll));

        Entity updated = Assert.Single(svc.Updated);
        Assert.Equal(existingId, updated.Id);
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(dll)), updated.GetAttributeValue<string>("content"));
        Assert.Equal(ExpectedVersion(dll), updated.GetAttributeValue<string>("version"));
        Assert.Equal("neutral", updated.GetAttributeValue<string>("culture"));
        Assert.Equal(ExpectedToken(dll), updated.GetAttributeValue<string>("publickeytoken"));
        Assert.NotEqual("null", updated.GetAttributeValue<string>("publickeytoken"));
        // Update must NOT re-stamp sourcetype/isolationmode (only set on create).
        Assert.False(updated.Contains("sourcetype"));
        Assert.False(updated.Contains("isolationmode"));
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void UpsertPluginAssembly_throws_when_assembly_file_missing()
    {
        var svc = new TestSupport.FakeOrgService();
        string missing = Path.Combine(Path.GetTempPath(), "no-asm-" + Guid.NewGuid().ToString("N") + ".dll");
        var ex = Assert.Throws<FileNotFoundException>(
            () => DataverseRegistration.UpsertPluginAssembly(svc, new AssemblyPayload("X", missing)));
        Assert.Contains("Assembly payload file not found", ex.Message);
    }

    // ---- GuardAgainstStaleAssemblyVersion -------------------------------------------------------

    [Fact]
    public void GuardAgainstStaleAssemblyVersion_returns_when_no_new_types()
    {
        var svc = new TestSupport.FakeOrgService();
        string dll = typeof(DataverseRegistration).Assembly.Location;
        DataverseRegistration.GuardAgainstStaleAssemblyVersion(svc, new AssemblyPayload("Vela.Cli", dll), Array.Empty<string>());
        Assert.Empty(svc.Queries); // short-circuits before any read
    }

    [Fact]
    public void GuardAgainstStaleAssemblyVersion_returns_when_assembly_not_yet_registered()
    {
        var svc = new TestSupport.FakeOrgService(); // GetPluginAssembly → empty
        string dll = typeof(DataverseRegistration).Assembly.Location;
        DataverseRegistration.GuardAgainstStaleAssemblyVersion(svc, new AssemblyPayload("Vela.Cli", dll), ["Vela.Cli.NewType"]);
        // no throw: first registration of this assembly
    }

    [Fact]
    public void GuardAgainstStaleAssemblyVersion_returns_when_version_bumped()
    {
        string dll = typeof(DataverseRegistration).Assembly.Location;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
            OnRetrieve = (entity, id, _) => new Entity(entity, id) { ["version"] = "0.0.0.1" }, // differs from live dll
        };
        DataverseRegistration.GuardAgainstStaleAssemblyVersion(svc, new AssemblyPayload("Vela.Cli", dll), ["Vela.Cli.NewType"]);
        // no throw: version differs → Dataverse will load the new assembly
    }

    [Fact]
    public void GuardAgainstStaleAssemblyVersion_throws_when_version_unchanged()
    {
        string dll = typeof(DataverseRegistration).Assembly.Location;
        string liveVersion = ExpectedVersion(dll);
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
            OnRetrieve = (entity, id, cols) =>
            {
                Assert.Contains("version", cols.Columns); // the live version is read from the version column
                return new Entity(entity, id) { ["version"] = liveVersion };
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.GuardAgainstStaleAssemblyVersion(svc, new AssemblyPayload("Vela.Cli", dll), ["Vela.Cli.NewType"]));
        // Each concatenated segment of the guidance message must survive (mutating any one to "" is caught).
        Assert.Contains("registers new plugin type", ex.Message);
        Assert.Contains("version is unchanged", ex.Message);
        Assert.Contains("Vela.Cli.NewType", ex.Message);
        Assert.Contains("Dataverse caches plugin assemblies by version", ex.Message);
        Assert.Contains("could not be", ex.Message);
        Assert.Contains("Bump the assembly version", ex.Message);
        Assert.Contains("scaffolds an auto-incrementing", ex.Message);
    }

    // ---- CreatePluginType -----------------------------------------------------------------------

    [Fact]
    public void CreatePluginType_creates_type_with_friendly_name_from_last_segment()
    {
        var svc = new TestSupport.FakeOrgService();
        var assemblyId = Guid.NewGuid();

        DataverseRegistration.CreatePluginType(svc, assemblyId, "Acme.Plugins.AccountPlugin");

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("plugintype", created.LogicalName);
        EntityReference asmRef = created.GetAttributeValue<EntityReference>("pluginassemblyid");
        Assert.Equal("pluginassembly", asmRef.LogicalName);
        Assert.Equal(assemblyId, asmRef.Id);
        Assert.Equal("Acme.Plugins.AccountPlugin", created.GetAttributeValue<string>("typename"));
        Assert.Equal("Acme.Plugins.AccountPlugin", created.GetAttributeValue<string>("name"));
        Assert.Equal("AccountPlugin", created.GetAttributeValue<string>("friendlyname"));
    }

    // ---- EnsurePluginTypesForMissingEntries -----------------------------------------------------

    [Fact]
    public void EnsurePluginTypesForMissingEntries_creates_missing_types_under_their_assembly()
    {
        var assemblyId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginassembly"
                ? TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", assemblyId))
                : new EntityCollection(), // plugintype → none exist yet
        };

        DataverseRegistration.EnsurePluginTypesForMissingEntries(
            svc, [new AssemblyPayload("Acme.Plugins", "unused")], ["Acme.Plugins.AccountPlugin"]);

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("plugintype", created.LogicalName);
        Assert.Equal("Acme.Plugins.AccountPlugin", created.GetAttributeValue<string>("typename"));
        Assert.Equal(assemblyId, created.GetAttributeValue<EntityReference>("pluginassemblyid").Id);
    }

    [Fact]
    public void EnsurePluginTypesForMissingEntries_noop_when_all_types_present()
    {
        var assemblyId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginassembly"
                ? TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", assemblyId))
                : TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Acme.Plugins.AccountPlugin" }),
        };

        DataverseRegistration.EnsurePluginTypesForMissingEntries(
            svc, [new AssemblyPayload("Acme.Plugins", "unused")], ["Acme.Plugins.AccountPlugin"]);

        Assert.Empty(svc.Created); // already present → nothing created
    }

    [Fact]
    public void EnsurePluginTypesForMissingEntries_creates_missing_types_in_ordinal_order()
    {
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginassembly"
                ? TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()))
                : new EntityCollection(),
        };

        // Passed unsorted; the helper orders missing types ordinally before creating them.
        DataverseRegistration.EnsurePluginTypesForMissingEntries(
            svc, [new AssemblyPayload("Acme.Plugins", "unused")], ["Acme.Plugins.Zeta", "Acme.Plugins.Alpha"]);

        Assert.Equal(
            ["Acme.Plugins.Alpha", "Acme.Plugins.Zeta"],
            svc.Created.Select(e => e.GetAttributeValue<string>("typename")));
    }

    // ---- WaitForPluginTypes / WaitForRequiredPluginTypes (success paths) -------------------------

    [Fact]
    public void WaitForPluginTypes_returns_immediately_when_types_present()
    {
        var assemblyId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginassembly"
                ? TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", assemblyId))
                : TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Acme.Plugins.AccountPlugin" }),
        };

        // Would Thread.Sleep for 30s on the failure path — completing quickly proves the success path is taken.
        DataverseRegistration.WaitForPluginTypes(svc, "Acme.Plugins", ["Acme.Plugins.AccountPlugin"]);
    }

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

    // ---- EnsurePluginAssembliesForMissingTypes (early returns) -----------------------------------

    [Fact]
    public void EnsurePluginAssembliesForMissingTypes_noop_when_no_payloads_or_no_required()
    {
        var svc = new TestSupport.FakeOrgService();
        DataverseRegistration.EnsurePluginAssembliesForMissingTypes(svc, Array.Empty<AssemblyPayload>(), ["X"]);
        DataverseRegistration.EnsurePluginAssembliesForMissingTypes(svc, [new AssemblyPayload("A", "p")], new HashSet<string>());
        Assert.Empty(svc.Queries);
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void EnsurePluginAssembliesForMissingTypes_noop_when_all_types_already_present()
    {
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginassembly"
                ? TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()))
                : TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Acme.Plugins.AccountPlugin" }),
        };

        DataverseRegistration.EnsurePluginAssembliesForMissingTypes(
            svc, [new AssemblyPayload("Acme.Plugins", "unused")], ["Acme.Plugins.AccountPlugin"]);

        // All required types already resolve → no assembly upload attempted.
        Assert.Empty(svc.Created);
        Assert.Empty(svc.Updated);
    }

    [Fact]
    public void EnsurePluginAssembliesForMissingTypes_guards_uploads_and_waits_for_new_type()
    {
        // The full sync path: a required type is initially absent, the assembly version differs from the live
        // one (guard passes), the assembly is re-uploaded, then the type materializes on the next poll.
        string dll = typeof(DataverseRegistration).Assembly.Location;
        int pluginTypeCalls = 0;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                switch (q.EntityName)
                {
                    case "pluginassembly":
                        return TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()));
                    case "plugintype":
                        // First lookup: type absent (drives the sync). Later polls: type has materialized.
                        pluginTypeCalls++;
                        return pluginTypeCalls == 1
                            ? new EntityCollection()
                            : TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Zava.Plugins.NewPlugin" });
                    default:
                        return new EntityCollection();
                }
            },
            OnRetrieve = (entity, id, _) => new Entity(entity, id) { ["version"] = "0.0.0.1" }, // live version differs → guard passes
        };

        DataverseRegistration.EnsurePluginAssembliesForMissingTypes(
            svc, [new AssemblyPayload("Zava.Plugins", dll)], ["Zava.Plugins.NewPlugin"]);

        // The existing assembly is re-uploaded (update path), not created.
        Assert.Contains(svc.Updated, e => e.LogicalName == "pluginassembly");
    }

    // ---- ApplyDiff: missing plugin type after sync ----------------------------------------------

    [Fact]
    public void ApplyDiff_throws_when_required_plugin_type_never_resolves()
    {
        // Two steps requiring plugin types that no assembly/package provides. With no packages and no source
        // assemblies, the assembly-sync fallback resolves nothing, so apply must fail listing every missing
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
                () => DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false));
            Assert.Contains("Plugin type not found in Dataverse after assembly sync", ex.Message);
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
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false));
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
        var removedApiId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = apiType }),
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

        DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false);

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
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = apiType }),
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
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false));
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
            OnRetrieveMultiple = q => q.EntityName == "sdkmessageprocessingstep"
                ? throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Invalid plug-in registration stage.")
                : new EntityCollection(),
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
            DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false); // must NOT throw
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
            DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false); // must NOT throw
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("skipping custom API delete", captured.ToString());
    }

    [Fact]
    public void ApplyDiff_midrun_failure_at_changed_step_counts_completed_creates()
    {
        // Removed step + two added steps (succeed) + one changed step whose message does not resolve (fails).
        // The completed count must include the two creates, proving each create's done++ landed.
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
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        // delete + publish + two creates completed; failure is on the changed step's update.
        Assert.Contains("completed (4):", stderr);
        Assert.Contains($"+ create step {plugin}", stderr);
        Assert.Contains($"failed at: update step {plugin}", stderr);
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

        DataverseRegistration.ApplyDiff(svc, diff, [], packages, solutionUniqueName: null, new List<string>(), verbose: false);

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
    public void ApplyDiff_resolves_partially_missing_types_via_assembly_sync()
    {
        // Two required types: one resolves up-front, one is initially absent and only materializes after the
        // assembly sync. Apply must enter the sync block (Any-missing is true), resolve the second type, and
        // succeed - the branch that skips the sync would leave it unresolved and throw.
        string dll = typeof(DataverseRegistration).Assembly.Location;
        const string typeA = "Zava.Plugins.A";
        const string typeB = "Zava.Plugins.B";
        TestSupport.FakeOrgService svc = null!;
        svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                switch (q.EntityName)
                {
                    case "pluginassembly": return TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()));
                    case "plugintype":
                        // A is always present; B only materializes once the assembly has actually been re-uploaded
                        // (so removing the sync step leaves B unresolved and apply throws).
                        var types = new List<Entity> { new Entity("plugintype", Guid.NewGuid()) { ["typename"] = typeA } };
                        if (svc.Updated.Any(e => e.LogicalName == "pluginassembly"))
                        {
                            types.Add(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = typeB });
                        }
                        return TestSupport.FakeOrgService.Collection(types.ToArray());
                    default: return new EntityCollection(); // customapi lookups empty → create
                }
            },
            OnRetrieve = (e, id, _) => new Entity(e, id) { ["version"] = "9.9.9.9" }, // differs from the dll → stale guard passes
        };

        JsonElement apiA = Json(new { pluginTypeName = typeA, uniqueName = "zava_A" });
        JsonElement apiB = Json(new { pluginTypeName = typeB, uniqueName = "zava_B" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("custom-api", typeA, apiA), new RegistrationItem("custom-api", typeB, apiB)],
            Removed: [],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);
        var sourceAssemblies = new[] { new AssemblyPayload("Zava.Plugins", dll) };

        DataverseRegistration.ApplyDiff(svc, diff, sourceAssemblies, [], null, new List<string>(), verbose: false); // must NOT throw

        // Both custom APIs were created once their types resolved.
        Assert.Equal(2, svc.Created.Count(e => e.LogicalName == "customapi"));
    }

    [Fact]
    public void ApplyDiff_resume_report_counts_custom_api_create_before_changed_failure()
    {
        // removed api (delete) + added api (create succeeds) + changed api (its lookup throws). The completed
        // count must include the created api, pinning the added-api done++ and the "update custom-api" label.
        const string apiType = "Zava.Plugins.MyApi";
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                switch (q.EntityName)
                {
                    case "pluginassembly": return TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()));
                    case "plugintype": return TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = apiType });
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
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("completed (3):", stderr);                    // delete + publish + create
        Assert.Contains($"+ create custom-api {apiType}", stderr);
        Assert.Contains($"failed at: update custom-api {apiType}", stderr);
    }

    [Fact]
    public void ApplyDiff_resume_report_counts_custom_api_update_before_step_failure()
    {
        // A changed custom-api updates successfully, then an added step fails. The completed count must include
        // the api update, pinning the changed-api done++ and the "update custom-api" completed label.
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
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("completed (2):", stderr);                 // publish + api update
        Assert.Contains($"+ update custom-api {type}", stderr);
        Assert.Contains($"failed at: create step {type}", stderr);
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
            DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false); // must NOT throw
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("skipping custom API upsert", stderr);   // added path
        Assert.Contains("skipping custom API update", stderr);   // changed path
    }

    [Fact]
    public void EnsurePluginAssembliesForMissingTypes_throws_stale_guard_for_matched_types()
    {
        // Two missing types match the payload assembly (one exact, one by the "AssemblyName." prefix). Both feed
        // the stale-version guard, which throws because the live version equals the payload's.
        string dll = typeof(DataverseRegistration).Assembly.Location;
        string liveVersion = ExpectedVersion(dll);
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginassembly"
                ? TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()))
                : new EntityCollection(),   // plugintype empty → both required are missing
            OnRetrieve = (e, id, _) => new Entity(e, id) { ["version"] = liveVersion },
        };

        var required = new HashSet<string> { "Zava.Plugins", "Zava.Plugins.Foo" };
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.EnsurePluginAssembliesForMissingTypes(svc, [new AssemblyPayload("Zava.Plugins", dll)], required));
        Assert.Contains("version is unchanged", ex.Message);
        // Both matched types are listed (ordinal order), proving the exact + prefix match both landed.
        int a = ex.Message.IndexOf("Zava.Plugins,", StringComparison.Ordinal);
        int b = ex.Message.IndexOf("Zava.Plugins.Foo", StringComparison.Ordinal);
        Assert.True(a >= 0 && b >= 0 && a < b, "both matched types listed in ordinal order");
    }

    [Fact]
    public void UpsertPluginPackage_updates_existing_package_with_solutionid()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("pkg-bytes-3");
        string path = NewTempFile(bytes);
        var existingId = Guid.NewGuid();
        var solutionId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(new Entity("pluginpackage", existingId)),
        };

        var payload = new PluginPackagePayload("acme_Pkg", "3.0.0", path);
        Guid result = DataverseRegistration.UpsertPluginPackage(svc, payload, solutionPrefix: null, solutionId, new List<string>(), verbose: false);

        Assert.Equal(existingId, result);
        Entity updated = Assert.Single(svc.Updated);
        EntityReference solutionRef = updated.GetAttributeValue<EntityReference>("solutionid");
        Assert.Equal("solution", solutionRef.LogicalName);
        Assert.Equal(solutionId, solutionRef.Id);
    }

    // ---- fixtures --------------------------------------------------------------------------------

    private static string ExpectedVersion(string dll)
        => AssemblyName.GetAssemblyName(dll).Version?.ToString() ?? "1.0.0.0";

    private static string ExpectedToken(string dll)
    {
        byte[]? token = AssemblyName.GetAssemblyName(dll).GetPublicKeyToken();
        return token == null || token.Length == 0 ? "null" : Convert.ToHexString(token).ToLowerInvariant();
    }

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
