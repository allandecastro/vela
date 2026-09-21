using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Drift check (<c>vela models --verify</c>): both sides go through the real <see cref="ModelGenerator"/>, so the
/// extractor is exercised against exactly the source the generator emits, and the comparison catches a column the
/// live schema dropped, a retyped column, or a column added live.
/// </summary>
public class ModelDriftCheckerTests
{
    private const string Ns = "Acme.Shared.Models.Generated";

    private static string Generate(MetadataFactory entity, string fileName = "Account.cs") =>
        ModelGenerator.Generate(new[] { entity.Build() }, new ModelGenOptions(Ns))
            .Single(m => m.FileName == fileName).Code;

    private static MetadataFactory Account() =>
        MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("name", "Name", maxLength: 160)
            .Money("revenue", "Revenue")
            .Lookup("primarycontactid", "PrimaryContactId", "contact");

    // ---- Extract ------------------------------------------------------------------------------------

    [Fact]
    public void Extract_reads_the_entity_logical_name_and_attribute_types()
    {
        ModelDriftChecker.ExtractedEntity? extracted = ModelDriftChecker.Extract(Generate(Account()));

        Assert.NotNull(extracted);
        Assert.Equal("account", extracted!.EntityLogicalName);
        Assert.Equal("string", extracted.Attributes["name"]);
        Assert.Equal("Money", extracted.Attributes["revenue"]);
        Assert.Equal("EntityReference", extracted.Attributes["primarycontactid"]);
    }

    [Fact]
    public void Extract_returns_null_for_a_source_without_an_entity_class()
    {
        Assert.Null(ModelDriftChecker.Extract("namespace X { public static class Roles { } }"));
        Assert.Null(ModelDriftChecker.Extract(string.Empty));
    }

    // ---- Compare ------------------------------------------------------------------------------------

    [Fact]
    public void Compare_reports_in_sync_when_the_model_matches_the_schema()
    {
        string code = Generate(Account());

        ModelDriftChecker.ModelDriftReport report = ModelDriftChecker.Compare([code], [code]);

        Assert.False(report.HasDrift);
        ModelDriftChecker.EntityDrift drift = Assert.Single(report.Entities);
        Assert.False(drift.HasDrift);
    }

    [Fact]
    public void Compare_flags_a_column_removed_in_dataverse()
    {
        // Local model still has "fax"; the live schema no longer does -> the typed-image blind spot.
        string live = Generate(Account());
        string local = Generate(Account().String("fax", "Fax", maxLength: 50));

        ModelDriftChecker.ModelDriftReport report = ModelDriftChecker.Compare([live], [local]);

        Assert.True(report.HasDrift);
        ModelDriftChecker.EntityDrift drift = Assert.Single(report.Entities);
        Assert.Equal("fax", Assert.Single(drift.RemovedInDataverse));
        Assert.Empty(drift.AddedInDataverse);
        Assert.Empty(drift.TypeChanged);
    }

    [Fact]
    public void Compare_flags_a_column_added_in_dataverse()
    {
        string live = Generate(Account().String("website", "Website", maxLength: 200));
        string local = Generate(Account());

        ModelDriftChecker.ModelDriftReport report = ModelDriftChecker.Compare([live], [local]);

        Assert.True(report.HasDrift);
        ModelDriftChecker.EntityDrift drift = Assert.Single(report.Entities);
        Assert.Equal("website", Assert.Single(drift.AddedInDataverse));
        Assert.Empty(drift.RemovedInDataverse);
    }

    [Fact]
    public void Compare_flags_a_retyped_column()
    {
        // "qty" is an integer locally but a decimal live: int? -> decimal?.
        string live = Generate(Account().Decimal("qty", "Qty"));
        string local = Generate(Account().Integer("qty", "Qty"));

        ModelDriftChecker.ModelDriftReport report = ModelDriftChecker.Compare([live], [local]);

        Assert.True(report.HasDrift);
        ModelDriftChecker.EntityDrift drift = Assert.Single(report.Entities);
        ModelDriftChecker.AttributeTypeChange change = Assert.Single(drift.TypeChanged);
        Assert.Equal("qty", change.LogicalName);
        Assert.Equal("int?", change.LocalType);
        Assert.Equal("decimal?", change.LiveType);
    }

    [Fact]
    public void Compare_flags_a_missing_local_file()
    {
        string live = Generate(Account());

        ModelDriftChecker.ModelDriftReport report = ModelDriftChecker.Compare([live], []);

        Assert.True(report.HasDrift);
        ModelDriftChecker.EntityDrift drift = Assert.Single(report.Entities);
        Assert.True(drift.MissingLocalFile);
    }

    [Fact]
    public void Compare_ignores_local_files_for_entities_outside_the_verified_scope()
    {
        // Local has an extra table (contact) that the live scope does not include; it must not be reported.
        string live = Generate(Account());
        string localContact = Generate(
            MetadataFactory.Entity("contact", "Contact").PrimaryId("contactid", "ContactId").String("fullname", "FullName"),
            "Contact.cs");

        ModelDriftChecker.ModelDriftReport report = ModelDriftChecker.Compare([live], [live, localContact]);

        Assert.False(report.HasDrift);
        Assert.Single(report.Entities);
        Assert.Equal("account", report.Entities[0].EntityLogicalName);
    }
}
