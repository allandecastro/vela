using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class FormXmlParserTests
{
    // A compact but representative Main form: two tabs, three sections, labels, a required override, a subgrid
    // (class id + parameters) and a header field. Mirrors the shape real FormXML exports carry.
    private const string ContactMainFormXml = """
        <form>
          <tabs>
            <tab name="general">
              <labels><label description="General" languagecode="1033" /></labels>
              <columns>
                <column width="100%">
                  <sections>
                    <section name="contactInfo">
                      <labels><label description="Contact" languagecode="1033" /></labels>
                      <rows>
                        <row>
                          <cell>
                            <labels><label description="First Name" languagecode="1033" /></labels>
                            <control id="firstname" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="firstname" />
                          </cell>
                        </row>
                        <row>
                          <cell>
                            <labels><label description="Last Name" languagecode="1033" /></labels>
                            <control id="lastname" datafieldname="lastname" isrequired="true" />
                          </cell>
                        </row>
                        <row>
                          <cell>
                            <control id="spacer1" classid="{5C5600E0-1D6E-4205-A272-BE80DA87FD42}" />
                          </cell>
                          <cell>
                            <control id="Contacts" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}">
                              <parameters>
                                <TargetEntityType>opportunity</TargetEntityType>
                                <RelationshipName>opportunity_parent_contact</RelationshipName>
                                <ViewId>{00000000-0000-0000-0000-0000000000A1}</ViewId>
                              </parameters>
                            </control>
                          </cell>
                        </row>
                      </rows>
                    </section>
                  </sections>
                </column>
              </columns>
            </tab>
            <tab name="details">
              <labels><label description="Details" languagecode="1033" /></labels>
              <columns>
                <column width="100%">
                  <sections>
                    <section name="finance">
                      <labels><label description="Finance" languagecode="1033" /></labels>
                      <rows>
                        <row>
                          <cell>
                            <control id="creditlimit" datafieldname="creditlimit" />
                          </cell>
                        </row>
                      </rows>
                    </section>
                  </sections>
                </column>
              </columns>
            </tab>
          </tabs>
          <header id="{00000000-0000-0000-0000-00000000ffff}">
            <rows>
              <row>
                <cell>
                  <control id="ownerid" datafieldname="ownerid" />
                </cell>
              </row>
            </rows>
          </header>
        </form>
        """;

    [Fact]
    public void Parses_Tabs_Sections_And_Fields_In_Order()
    {
        ParsedForm form = FormXmlParser.Parse(ContactMainFormXml);

        Assert.Equal(new[] { "general", "details" }, form.Tabs.Select(t => t.Name));
        Assert.Equal("General", form.Tabs[0].Label);

        FormSection contactInfo = form.Tabs[0].Sections.Single();
        Assert.Equal("contactInfo", contactInfo.Name);
        Assert.Equal("Contact", contactInfo.Label);
        Assert.Equal(new[] { "firstname", "lastname" }, contactInfo.Cells.Select(c => c.DataFieldName));

        FormSection finance = form.Tabs[1].Sections.Single();
        Assert.Equal(new[] { "creditlimit" }, finance.Cells.Select(c => c.DataFieldName));
    }

    [Fact]
    public void Reads_Field_Label_And_Required_Override()
    {
        ParsedForm form = FormXmlParser.Parse(ContactMainFormXml);
        FormFieldCell[] cells = form.Tabs[0].Sections.Single().Cells.ToArray();

        Assert.Equal("First Name", cells[0].Label);
        Assert.Null(cells[0].Required);

        Assert.Equal("Last Name", cells[1].Label);
        Assert.True(cells[1].Required);
    }

    [Fact]
    public void Collects_Subgrid_With_Target_Relationship_And_View()
    {
        ParsedForm form = FormXmlParser.Parse(ContactMainFormXml);

        FormSubgrid subgrid = Assert.Single(form.Subgrids);
        Assert.Equal("Contacts", subgrid.Name);
        Assert.Equal("opportunity", subgrid.TargetEntity);
        Assert.Equal("opportunity_parent_contact", subgrid.RelationshipName);
        Assert.Equal("00000000-0000-0000-0000-0000000000a1", subgrid.ViewId);
    }

    [Fact]
    public void Skips_Spacer_And_Non_Bound_Controls()
    {
        ParsedForm form = FormXmlParser.Parse(ContactMainFormXml);

        // The spacer control (no datafieldname, not a subgrid) must not appear as a field.
        Assert.DoesNotContain(
            form.Tabs.SelectMany(t => t.Sections).SelectMany(s => s.Cells),
            c => c.DataFieldName == "spacer1");
    }

    [Fact]
    public void Reads_Header_Fields_Without_Location()
    {
        ParsedForm form = FormXmlParser.Parse(ContactMainFormXml);

        FormFieldCell header = Assert.Single(form.HeaderFields);
        Assert.Equal("ownerid", header.DataFieldName);
        Assert.Empty(form.FooterFields);
    }

    [Fact]
    public void Detects_Subgrid_By_Parameters_When_ClassId_Absent()
    {
        // Some exports omit/rename the class id; a TargetEntityType parameter alone still marks a subgrid. The
        // relationship falls back to the control's `relationship` attribute when the parameter is absent.
        const string xml = """
            <form>
              <tabs>
                <tab name="t">
                  <columns><column><sections>
                    <section name="s">
                      <rows><row>
                        <cell>
                          <control id="Cases" indicationOfSubgrid="true" relationship="incident_customer_accounts">
                            <parameters>
                              <TargetEntityType>incident</TargetEntityType>
                            </parameters>
                          </control>
                        </cell>
                      </row></rows>
                    </section>
                  </sections></column></columns>
                </tab>
              </tabs>
            </form>
            """;

        ParsedForm form = FormXmlParser.Parse(xml);

        FormSubgrid subgrid = Assert.Single(form.Subgrids);
        Assert.Equal("Cases", subgrid.Name);
        Assert.Equal("incident", subgrid.TargetEntity);
        Assert.Equal("incident_customer_accounts", subgrid.RelationshipName);
        Assert.Null(subgrid.ViewId);
    }

    [Fact]
    public void Falls_Back_To_First_Label_When_No_Base_Language()
    {
        const string xml = """
            <form>
              <tabs><tab name="t">
                <labels><label description="Onglet" languagecode="1036" /></labels>
                <columns><column><sections>
                  <section name="s">
                    <rows><row><cell>
                      <control id="name" datafieldname="name" />
                    </cell></row></rows>
                  </section>
                </sections></column></columns>
              </tab></tabs>
            </form>
            """;

        ParsedForm form = FormXmlParser.Parse(xml);
        Assert.Equal("Onglet", form.Tabs.Single().Label);
    }

    [Fact]
    public void Empty_FormXml_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => FormXmlParser.Parse("   "));
    }

    [Fact]
    public void Corpus_Is_Schema_Valid()
    {
        // The parser corpus must be real FormXML: it validates against the official Microsoft FormXml.xsd.
        Assert.True(FormXmlSchema.IsValid(ContactMainFormXml), string.Join("\n", FormXmlSchema.Validate(ContactMainFormXml)));
    }

    [Fact]
    public void Flattens_Sections_Across_Multiple_Columns_In_Document_Order()
    {
        // A tab can lay sections out across several columns; the parser flattens them in document order.
        const string xml = """
            <form>
              <tabs>
                <tab name="general">
                  <columns>
                    <column width="50%">
                      <sections>
                        <section name="left">
                          <rows><row><cell><control datafieldname="a" /></cell></row></rows>
                        </section>
                      </sections>
                    </column>
                    <column width="50%">
                      <sections>
                        <section name="right">
                          <rows><row><cell><control datafieldname="b" /></cell></row></rows>
                        </section>
                      </sections>
                    </column>
                  </columns>
                </tab>
              </tabs>
            </form>
            """;

        ParsedForm form = FormXmlParser.Parse(xml);

        Assert.Equal(new[] { "left", "right" }, form.Tabs.Single().Sections.Select(s => s.Name));
        Assert.True(FormXmlSchema.IsValid(xml), string.Join("\n", FormXmlSchema.Validate(xml)));
    }
}
