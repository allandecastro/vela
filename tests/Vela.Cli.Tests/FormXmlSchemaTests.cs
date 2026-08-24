using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Validates <see cref="FormXmlSchema"/> against the official Microsoft FormXml.xsd: a realistic form validates, and
/// malformed / schema-invalid inputs surface errors. This also anchors the parser corpus on the real schema.
/// </summary>
public class FormXmlSchemaTests
{
    // A minimal but schema-shaped Main form (tabs -> columns -> column -> sections -> section -> rows -> row -> cell).
    private const string ValidFormXml = """
        <form>
          <tabs>
            <tab name="general">
              <labels><label description="General" languagecode="1033" /></labels>
              <columns>
                <column width="100%">
                  <sections>
                    <section name="s">
                      <labels><label description="Section" languagecode="1033" /></labels>
                      <rows>
                        <row>
                          <cell>
                            <labels><label description="Name" languagecode="1033" /></labels>
                            <control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name" />
                          </cell>
                        </row>
                      </rows>
                    </section>
                  </sections>
                </column>
              </columns>
            </tab>
          </tabs>
        </form>
        """;

    [Fact]
    public void Valid_FormXml_Has_No_Errors()
    {
        Assert.True(FormXmlSchema.IsValid(ValidFormXml), string.Join("\n", FormXmlSchema.Validate(ValidFormXml)));
    }

    [Fact]
    public void Missing_Required_Tabs_Is_Invalid()
    {
        // `tabs` is minOccurs=1 on the form; an empty form must fail schema validation.
        Assert.NotEmpty(FormXmlSchema.Validate("<form></form>"));
    }

    [Fact]
    public void Unknown_Element_Is_Invalid()
    {
        const string xml = """
            <form>
              <tabs>
                <tab name="general">
                  <columns><column><sections><section name="s">
                    <rows><row><cell><bogus /></cell></row></rows>
                  </section></sections></column></columns>
                </tab>
              </tabs>
            </form>
            """;

        Assert.NotEmpty(FormXmlSchema.Validate(xml));
    }

    [Fact]
    public void Malformed_Xml_Reports_A_Single_Error()
    {
        string error = Assert.Single(FormXmlSchema.Validate("<form><tabs>"));
        Assert.Contains("Malformed XML", error);
    }

    [Fact]
    public void Empty_Input_Is_Invalid()
    {
        Assert.NotEmpty(FormXmlSchema.Validate("   "));
    }
}
