using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Offline metadata reader (<c>vela models --from-solution</c>): parses a solution's customizations.xml into
/// <see cref="EntityMetadata"/> that the same <see cref="ModelGenerator"/> consumes. The fixture follows the
/// documented customizations.xml schema (custom entity with representative column types, a local and a global option
/// set, a companion column, an unmapped type, and a system-table shell that must be skipped).
/// </summary>
public class SolutionMetadataReaderTests
{
    private const string Fixture = """
        <?xml version="1.0" encoding="utf-8"?>
        <ImportExportXml xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" OrganizationVersion="9.2.0.0">
          <Entities>
            <Entity>
              <Name LocalizedName="Widget" OriginalName="Widget">adc_Widget</Name>
              <EntityInfo>
                <entity Name="adc_widget">
                  <LocalizedNames>
                    <LocalizedName description="Widget" languagecode="1033" />
                  </LocalizedNames>
                  <attributes>
                    <attribute PhysicalName="adc_WidgetId">
                      <Type>primarykey</Type>
                      <LogicalName>adc_widgetid</LogicalName>
                      <RequiredLevel>systemrequired</RequiredLevel>
                    </attribute>
                    <attribute PhysicalName="adc_Name">
                      <Type>nvarchar</Type>
                      <LogicalName>adc_name</LogicalName>
                      <MaxLength>100</MaxLength>
                      <RequiredLevel>applicationrequired</RequiredLevel>
                      <displaynames><displayname description="Name" languagecode="1033" /></displaynames>
                      <Descriptions><Description description="The widget name" languagecode="1033" /></Descriptions>
                    </attribute>
                    <attribute PhysicalName="adc_Count">
                      <Type>int</Type>
                      <LogicalName>adc_count</LogicalName>
                      <MinValue>0</MinValue>
                      <MaxValue>100</MaxValue>
                    </attribute>
                    <attribute PhysicalName="adc_Price">
                      <Type>money</Type>
                      <LogicalName>adc_price</LogicalName>
                    </attribute>
                    <attribute PhysicalName="adc_IsActive">
                      <Type>bit</Type>
                      <LogicalName>adc_isactive</LogicalName>
                    </attribute>
                    <attribute PhysicalName="adc_DueOn">
                      <Type>datetime</Type>
                      <LogicalName>adc_dueon</LogicalName>
                    </attribute>
                    <attribute PhysicalName="adc_AccountId">
                      <Type>lookup</Type>
                      <LogicalName>adc_accountid</LogicalName>
                      <LookupTypes><LookupType id="00000000-0000-0000-0000-000000000000">1</LookupType></LookupTypes>
                    </attribute>
                    <attribute PhysicalName="adc_Color">
                      <Type>picklist</Type>
                      <LogicalName>adc_color</LogicalName>
                      <optionset Name="adc_widget_adc_color" localizedName="Color">
                        <OptionSetType>picklist</OptionSetType>
                        <IsGlobal>0</IsGlobal>
                        <options>
                          <option value="1"><labels><label description="Red" languagecode="1033" /></labels></option>
                          <option value="2"><labels><label description="Green" languagecode="1033" /></labels></option>
                        </options>
                      </optionset>
                    </attribute>
                    <attribute PhysicalName="adc_Status">
                      <Type>picklist</Type>
                      <LogicalName>adc_status</LogicalName>
                      <OptionSetName>adc_globalstatus</OptionSetName>
                    </attribute>
                    <attribute PhysicalName="adc_ColorName">
                      <Type>nvarchar</Type>
                      <LogicalName>adc_colorname</LogicalName>
                      <AttributeOf>adc_color</AttributeOf>
                    </attribute>
                    <attribute PhysicalName="adc_Zone">
                      <Type>timezone</Type>
                      <LogicalName>adc_zone</LogicalName>
                    </attribute>
                  </attributes>
                </entity>
              </EntityInfo>
            </Entity>
            <Entity>
              <Name LocalizedName="Account" OriginalName="Account">Account</Name>
              <EntityInfo>
                <entity Name="account" unmodified="1">
                  <attributes />
                </entity>
              </EntityInfo>
            </Entity>
          </Entities>
          <optionsets>
            <optionset Name="adc_globalstatus" localizedName="Global Status">
              <OptionSetType>picklist</OptionSetType>
              <IsGlobal>1</IsGlobal>
              <options>
                <option value="10"><labels><label description="Open" languagecode="1033" /></labels></option>
                <option value="20"><labels><label description="Closed" languagecode="1033" /></labels></option>
              </options>
            </optionset>
          </optionsets>
          <EntityRelationships />
          <Languages><Language>1033</Language></Languages>
        </ImportExportXml>
        """;

    private static EntityMetadata Widget()
    {
        var entities = SolutionMetadataReader.ReadFromCustomizationsXml(Fixture);
        return Assert.Single(entities);
    }

    [Fact]
    public void Skips_system_table_shells_and_returns_only_custom_tables()
    {
        // The account shell (unmodified, no attributes) is dropped; only adc_widget remains.
        EntityMetadata widget = Widget();
        Assert.Equal("adc_widget", widget.LogicalName);
        Assert.Equal("adc_Widget", widget.SchemaName);
        Assert.Equal("adc_widgetid", widget.PrimaryIdAttribute);
    }

    [Fact]
    public void Maps_scalar_column_types_and_facets()
    {
        var code = ModelGenerator.Generate(new[] { Widget() }, new ModelGenOptions("Acme.Models")).Single(m => m.FileName == "adc_Widget.cs").Code;

        Assert.Contains("public const string EntityLogicalNameValue = \"adc_widget\";", code);
        Assert.Contains("[StringLength(100)]", code);
        Assert.Contains("public string adc_Name", code); // nvarchar -> string, schema-cased property
        Assert.Contains("[Range(0, 100)]", code);
        Assert.Contains("public int? adc_Count", code);
        Assert.Contains("public Money adc_Price", code);
        Assert.Contains("public bool? adc_IsActive", code);
        Assert.Contains("public DateTime? adc_DueOn", code);
    }

    [Fact]
    public void Emits_required_level_facet()
    {
        var code = ModelGenerator.Generate(new[] { Widget() }, new ModelGenOptions("Acme.Models")).Single(m => m.FileName == "adc_Widget.cs").Code;
        Assert.Contains("ApplicationRequired", code);
    }

    [Fact]
    public void Lookup_without_offline_targets_is_a_plain_entity_reference()
    {
        var code = ModelGenerator.Generate(new[] { Widget() }, new ModelGenOptions("Acme.Models")).Single(m => m.FileName == "adc_Widget.cs").Code;

        Assert.Contains("public EntityReference adc_AccountId", code);
        // No typed targets are available offline (the export stores numeric type codes, not logical names).
        Assert.DoesNotContain("[LookupTargets", code);
    }

    [Fact]
    public void Local_option_set_becomes_a_nested_enum()
    {
        var code = ModelGenerator.Generate(new[] { Widget() }, new ModelGenOptions("Acme.Models")).Single(m => m.FileName == "adc_Widget.cs").Code;

        Assert.Contains("Red = 1,", code);
        Assert.Contains("Green = 2,", code);
    }

    [Fact]
    public void Global_option_set_becomes_a_shared_enum_file()
    {
        var files = ModelGenerator.Generate(new[] { Widget() }, new ModelGenOptions("Acme.Models"));
        GeneratedModel global = Assert.Single(files, f => f.Code.Contains("Open = 10,"));

        Assert.Contains("Closed = 20,", global.Code);
    }

    [Fact]
    public void Companion_and_unmapped_columns_are_skipped()
    {
        EntityMetadata widget = Widget();
        // adc_colorname (AttributeOf) is preserved but marked as a companion (the generator skips it); adc_zone
        // (timezone, unmapped) is dropped entirely.
        Assert.Contains(widget.Attributes, a => a.LogicalName == "adc_colorname" && !string.IsNullOrEmpty(a.AttributeOf));
        Assert.DoesNotContain(widget.Attributes, a => a.LogicalName == "adc_zone");
    }

    [Fact]
    public void Entity_filter_narrows_the_generated_tables()
    {
        Assert.Empty(SolutionMetadataReader.ReadFromCustomizationsXml(Fixture, new[] { "adc_other" }));
        Assert.Single(SolutionMetadataReader.ReadFromCustomizationsXml(Fixture, new[] { "adc_widget" }));
    }

    [Fact]
    public void Reads_from_a_solution_zip()
    {
        string zip = Path.Combine(Path.GetTempPath(), $"vela-sol-{Guid.NewGuid():N}.zip");
        try
        {
            using (FileStream fs = new FileStream(zip, FileMode.Create))
            using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("customizations.xml");
                using StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(Fixture);
            }

            var entities = SolutionMetadataReader.ReadFromSolutionZip(zip);
            EntityMetadata widget = Assert.Single(entities);
            Assert.Equal("adc_widget", widget.LogicalName);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void A_zip_without_customizations_xml_is_rejected()
    {
        string zip = Path.Combine(Path.GetTempPath(), $"vela-sol-{Guid.NewGuid():N}.zip");
        try
        {
            using (FileStream fs = new FileStream(zip, FileMode.Create))
            using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                archive.CreateEntry("solution.xml");
            }

            Assert.Throws<InvalidOperationException>(() => SolutionMetadataReader.ReadFromSolutionZip(zip));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void Empty_or_missing_input_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SolutionMetadataReader.ReadFromCustomizationsXml("   "));
        Assert.Throws<ArgumentException>(() => SolutionMetadataReader.ReadFromSolutionZip(""));
    }
}
