using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Golden tests for <see cref="FormModelTsGenerator"/>: a parsed FormXML layout joined with table metadata must emit
/// the vela-client FormModel contract shape (see <c>vela-client/src/form/model.ts</c> and its fixtures). The emitter
/// is deterministic - fields follow the form's layout order and option sets follow first-reference order.
/// </summary>
public class FormModelTsGeneratorTests
{
    private const string ContactFormXml = """
        <form>
          <tabs>
            <tab name="general">
              <labels><label description="General" languagecode="1033" /></labels>
              <columns><column width="100%"><sections>
                <section name="contactInfo">
                  <labels><label description="Contact" languagecode="1033" /></labels>
                  <rows>
                    <row><cell><control datafieldname="firstname" /></cell></row>
                    <row><cell><control datafieldname="lastname" /></cell></row>
                    <row><cell><control datafieldname="emailaddress1" /></cell></row>
                    <row><cell><control datafieldname="birthdate" /></cell></row>
                    <row><cell><control datafieldname="gendercode" /></cell></row>
                    <row><cell><control datafieldname="statuscode" /></cell></row>
                    <row><cell><control datafieldname="donotemail" /></cell></row>
                    <row><cell><control datafieldname="parentcustomerid" /></cell></row>
                    <row><cell>
                      <control id="Opportunities" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}">
                        <parameters>
                          <TargetEntityType>opportunity</TargetEntityType>
                          <RelationshipName>opportunity_parent_contact</RelationshipName>
                        </parameters>
                      </control>
                    </cell></row>
                  </rows>
                </section>
              </sections></column></columns>
            </tab>
            <tab name="details">
              <labels><label description="Details" languagecode="1033" /></labels>
              <columns><column width="100%"><sections>
                <section name="finance">
                  <labels><label description="Finance" languagecode="1033" /></labels>
                  <rows>
                    <row><cell><control datafieldname="creditlimit" /></cell></row>
                  </rows>
                </section>
              </sections></column></columns>
            </tab>
          </tabs>
        </form>
        """;

    private const string AccountFormXml = """
        <form>
          <tabs>
            <tab name="general">
              <labels><label description="General" languagecode="1033" /></labels>
              <columns><column width="100%"><sections>
                <section name="accountInfo">
                  <labels><label description="Account Information" languagecode="1033" /></labels>
                  <rows>
                    <row><cell><control datafieldname="name" /></cell></row>
                    <row><cell><control datafieldname="accountnumber" /></cell></row>
                    <row><cell><control datafieldname="description" /></cell></row>
                    <row><cell><control datafieldname="new_establisheddate" /></cell></row>
                    <row><cell><control datafieldname="lastonholdtime" /></cell></row>
                    <row><cell><control datafieldname="industrycode" /></cell></row>
                    <row><cell><control datafieldname="statuscode" /></cell></row>
                    <row><cell><control datafieldname="primarycontactid" /></cell></row>
                    <row><cell><control datafieldname="entityimage" /></cell></row>
                  </rows>
                </section>
                <section name="preferences">
                  <labels><label description="Preferences" languagecode="1033" /></labels>
                  <rows>
                    <row><cell><control datafieldname="donotpostalmail" /></cell></row>
                    <row><cell><control datafieldname="new_servicetypes" /></cell></row>
                    <row><cell><control datafieldname="new_projectteam" /></cell></row>
                  </rows>
                </section>
              </sections></column></columns>
            </tab>
            <tab name="details">
              <labels><label description="Details" languagecode="1033" /></labels>
              <columns><column width="100%"><sections>
                <section name="demographics">
                  <labels><label description="Demographics" languagecode="1033" /></labels>
                  <rows>
                    <row><cell><control datafieldname="numberofemployees" /></cell></row>
                    <row><cell><control datafieldname="new_globalrank" /></cell></row>
                    <row><cell><control datafieldname="new_conversionrate" /></cell></row>
                    <row><cell><control datafieldname="new_creditscore" /></cell></row>
                  </rows>
                </section>
                <section name="finance">
                  <labels><label description="Finance" languagecode="1033" /></labels>
                  <rows>
                    <row><cell><control datafieldname="revenue" /></cell></row>
                    <row><cell><control datafieldname="accountid" /></cell></row>
                    <row><cell><control datafieldname="new_contractfile" /></cell></row>
                    <row><cell>
                      <control id="Contacts" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}">
                        <parameters>
                          <TargetEntityType>contact</TargetEntityType>
                          <RelationshipName>contact_customer_accounts</RelationshipName>
                          <ViewId>{00000000-0000-0000-0000-0000000000C1}</ViewId>
                        </parameters>
                      </control>
                    </cell></row>
                    <row><cell>
                      <control id="Cases" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}">
                        <parameters>
                          <TargetEntityType>incident</TargetEntityType>
                          <RelationshipName>incident_customer_accounts</RelationshipName>
                        </parameters>
                      </control>
                    </cell></row>
                  </rows>
                </section>
              </sections></column></columns>
            </tab>
          </tabs>
        </form>
        """;

    private static FormSource ContactForm()
    {
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .String("firstname", "FirstName").Display("First Name")
            .String("lastname", "LastName").Display("Last Name").Required(AttributeRequiredLevel.ApplicationRequired)
            .String("emailaddress1", "EMailAddress1").Display("Email")
            .DateTime("birthdate", "BirthDate").Display("Birthday")
            .Choice("gendercode", "GenderCode", global: false, null, (1, "Male"), (2, "Female")).Display("Gender")
            .Choice("statuscode", "StatusCode", global: false, null, (1, "Active"), (2, "Inactive")).Display("Status").Required(AttributeRequiredLevel.ApplicationRequired)
            .Boolean("donotemail", "DoNotEMail").Display("Do Not Email")
            .Customer("parentcustomerid", "ParentCustomerId", "account", "contact").Display("Company")
            .Money("creditlimit", "CreditLimit").Display("Credit Limit")
            .Build();

        return new FormSource("7c1e3f2a-0000-0000-0000-000000000001", "Main", "Information", FormXmlParser.Parse(ContactFormXml), entity);
    }

    private static FormSource AccountForm()
    {
        EntityMetadata entity = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId").Display("Account")
            .String("name", "Name", maxLength: 160).Display("Account Name").Required(AttributeRequiredLevel.ApplicationRequired)
            .String("accountnumber", "AccountNumber", maxLength: 20).Display("Account Number")
            .Memo("description", "Description", maxLength: 2000, format: StringFormat.TextArea).Display("Description")
            .DateTime("new_establisheddate", "new_EstablishedDate", DateTimeFormat.DateOnly).Display("Established On")
            .DateTime("lastonholdtime", "LastOnHoldTime", DateTimeFormat.DateAndTime).Display("Last On Hold Time")
            .Choice("industrycode", "IndustryCode", global: false, null, (1, "Accounting"), (2, "Consulting"), (3, "Manufacturing"), (4, "Retail"), (5, "Technology")).Display("Industry")
            .Choice("statuscode", "StatusCode", global: false, null, (1, "Active"), (2, "Inactive")).Display("Status Reason").Required(AttributeRequiredLevel.ApplicationRequired)
            .Lookup("primarycontactid", "PrimaryContactId", "contact", "account").Display("Primary Contact")
            .Image("entityimage", "EntityImage").Display("Logo")
            .Boolean("donotpostalmail", "DoNotPostalMail").Display("Do Not Send Mail")
            .MultiSelect("new_servicetypes", "new_ServiceTypes", global: false, null, (1, "Consulting"), (2, "Support"), (3, "Training"), (4, "Managed")).Display("Service Types")
            .PartyList("new_projectteam", "new_ProjectTeam", "systemuser", "team").Display("Project Team")
            .Integer("numberofemployees", "NumberOfEmployees", min: 0, max: 1000000).Display("Employees")
            .BigInt("new_globalrank", "new_GlobalRank", min: 0).Display("Global Rank")
            .Decimal("new_conversionrate", "new_ConversionRate", min: 0m, max: 1m, precision: 4).Display("Conversion Rate")
            .Double("new_creditscore", "new_CreditScore", precision: 2).Display("Credit Score")
            .Money("revenue", "Revenue", min: 0m, precision: 2).Display("Annual Revenue")
            .File("new_contractfile", "new_ContractFile").Display("Signed Contract")
            .Build();

        return new FormSource("7c1e3f2a-0000-0000-0000-000000000002", "Main", "Main", FormXmlParser.Parse(AccountFormXml), entity);
    }

    private static string Generate(FormSource form, string fileName) =>
        FormModelTsGenerator.Generate(new[] { form }).Single(m => m.FileName == fileName).Code;

    // Wraps a body-less section of field controls into a schema-valid single-tab form.
    private static string OneSection(string section, params string[] dataFields)
    {
        string rows = string.Concat(dataFields.Select(f => $"<row><cell><control datafieldname=\"{f}\" /></cell></row>"));
        return $"""
            <form><tabs><tab name="general">
              <labels><label description="General" languagecode="1033" /></labels>
              <columns><column width="100%"><sections><section name="{section}">
                <labels><label description="Sec" languagecode="1033" /></labels>
                <rows>{rows}</rows>
              </section></sections></column></columns>
            </tab></tabs></form>
            """;
    }

    [Fact]
    public void Header_Field_Has_No_Location_And_Is_Absent_From_Layout()
    {
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .String("firstname", "FirstName").Display("First Name")
            .Lookup("ownerid", "OwnerId", "systemuser", "team").Display("Owner")
            .Build();

        const string xml = """
            <form>
              <tabs><tab name="general">
                <labels><label description="General" languagecode="1033" /></labels>
                <columns><column width="100%"><sections><section name="s">
                  <labels><label description="Sec" languagecode="1033" /></labels>
                  <rows><row><cell><control datafieldname="firstname" /></cell></row></rows>
                </section></sections></column></columns>
              </tab></tabs>
              <header id="{00000000-0000-0000-0000-00000000ffff}">
                <rows><row><cell><control datafieldname="ownerid" /></cell></row></rows>
              </header>
            </form>
            """;

        Assert.True(FormXmlSchema.IsValid(xml), string.Join("\n", FormXmlSchema.Validate(xml)));
        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(xml), entity), "contact.info.form.ts");

        // ownerid is a header field: present in `fields`, but with no `at` and absent from the layout section list.
        Assert.Contains("ownerid: {", ts);
        Assert.DoesNotContain("'ownerid'", ts); // never listed in a section's fields array
        int ownerBlock = ts.IndexOf("ownerid: {", System.StringComparison.Ordinal);
        int nextClose = ts.IndexOf("},", ownerBlock, System.StringComparison.Ordinal);
        Assert.DoesNotContain("at: {", ts.Substring(ownerBlock, nextClose - ownerBlock));
    }

    [Fact]
    public void Global_Option_Set_Uses_Its_Own_Name_And_Is_Shared()
    {
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .Choice("preferredcolor", "PreferredColor", global: true, "vela_color", (1, "Red"), (2, "Green")).Display("Color")
            .Build();

        var files = FormModelTsGenerator.Generate(new[]
        {
            new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "preferredcolor")), entity),
        });

        string form = files.Single(f => f.FileName == "contact.info.form.ts").Code;
        string optionSets = files.Single(f => f.FileName == "option-sets.ts").Code;

        Assert.Contains("import { vela_color } from './option-sets';", form);
        Assert.Contains("options: vela_color,", form);
        Assert.Contains("export enum vela_color {", optionSets);
    }

    [Fact]
    public void QuickCreate_FormType_Is_Emitted_Verbatim()
    {
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .String("firstname", "FirstName").Display("First Name")
            .Build();

        string ts = Generate(new FormSource("id", "QuickCreate", "Quick", FormXmlParser.Parse(OneSection("s", "firstname")), entity), "contact.quick.form.ts");

        Assert.Contains("formType: 'QuickCreate',", ts);
    }

    [Fact]
    public void Duplicate_Field_Cells_Are_Emitted_Once()
    {
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .String("firstname", "FirstName").Display("First Name")
            .Build();

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "firstname", "firstname")), entity), "contact.info.form.ts");

        // The column appears twice in the FormXML but must be emitted once, and listed once in its section.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ts, "firstname").Count); // field key + section entry
    }

    [Fact]
    public void Same_Named_Forms_Get_Unique_File_And_Const_Names()
    {
        EntityMetadata entity = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("name", "Name").Display("Name")
            .Build();

        // A Main and a Quick Create form both literally named "Account" would slug to the same file/const.
        var files = FormModelTsGenerator.Generate(new[]
        {
            new FormSource("id1", "Main", "Account", FormXmlParser.Parse(OneSection("s", "name")), entity),
            new FormSource("id2", "QuickCreate", "Account", FormXmlParser.Parse(OneSection("s", "name")), entity),
        });

        Assert.Equal(
            new[] { "account.account.form.ts", "account.account-2.form.ts" },
            files.Select(f => f.FileName).ToArray());
        Assert.Contains("export const AccountAccount = {", files[0].Code);
        Assert.Contains("export const AccountAccount2 = {", files[1].Code);
    }

    [Fact]
    public void Generate_Null_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() => FormModelTsGenerator.Generate(null!));
    }

    [Fact]
    public void Form_With_Blank_Entity_Is_Skipped()
    {
        // A source whose metadata has no logical name is dropped rather than emitting a nameless file.
        EntityMetadata blank = new EntityMetadata();
        var files = FormModelTsGenerator.Generate(new[]
        {
            new FormSource("id", "Main", "X", FormXmlParser.Parse(OneSection("s")), blank),
        });

        Assert.Empty(files);
    }

    [Fact]
    public void Escapes_Quotes_And_Quotes_Non_Identifier_Keys()
    {
        EntityMetadata entity = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("name", "Name").Display("O'Brien & Co")
            .Build();

        // A subgrid whose control id isn't a valid JS identifier must be emitted as a quoted key.
        const string xml = """
            <form><tabs><tab name="general">
              <labels><label description="General" languagecode="1033" /></labels>
              <columns><column width="100%"><sections><section name="s">
                <labels><label description="Sec" languagecode="1033" /></labels>
                <rows>
                  <row><cell><control datafieldname="name" /></cell></row>
                  <row><cell>
                    <control id="My Grid" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}">
                      <parameters><TargetEntityType>contact</TargetEntityType></parameters>
                    </control>
                  </cell></row>
                </rows>
              </section></sections></column></columns>
            </tab></tabs></form>
            """;

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(xml), entity), "account.info.form.ts");

        Assert.Contains(@"label: 'O\'Brien & Co',", ts);
        Assert.Contains("'My Grid': { entity: 'contact' },", ts);
    }

    [Theory]
    [InlineData(StringFormat.Email, "email")]
    [InlineData(StringFormat.Url, "url")]
    [InlineData(StringFormat.Phone, "phone")]
    [InlineData(StringFormat.TickerSymbol, "ticker")]
    [InlineData(StringFormat.Text, "text")]
    public void String_Format_Is_Mapped(StringFormat format, string expected)
    {
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .String("field", "Field").StringFormat(format).Display("Field")
            .Build();

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "field")), entity), "contact.info.form.ts");

        Assert.Contains($"format: '{expected}',", ts);
    }

    [Fact]
    public void Unsupported_String_Format_Emits_No_Format()
    {
        // A format outside the FormModel union (e.g. RichText) maps to null: no `format` property is emitted.
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .String("field", "Field").StringFormat(StringFormat.RichText).Display("Field")
            .Build();

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "field")), entity), "contact.info.form.ts");

        Assert.DoesNotContain("format:", ts);
    }

    [Fact]
    public void Unmapped_Attribute_Type_Falls_Back_To_String_Kind()
    {
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .Unmapped("weird", "Weird").Display("Weird")
            .Build();

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "weird")), entity), "contact.info.form.ts");

        Assert.Contains("weird: {", ts);
        Assert.Contains("kind: 'string',", ts);
    }

    [Fact]
    public void Label_With_Newline_Is_Escaped()
    {
        // A label carrying a newline must not break the string literal (regression: TsString escapes control chars).
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .String("firstname", "FirstName").Display("First\nName")
            .Build();

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "firstname")), entity), "contact.info.form.ts");

        Assert.Contains(@"label: 'First\nName',", ts);
        Assert.DoesNotContain("First\nName", ts); // never a raw newline inside the literal
    }

    [Fact]
    public void Duplicate_Subgrid_Names_Are_Emitted_Once()
    {
        EntityMetadata entity = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("name", "Name").Display("Name")
            .Build();

        const string xml = """
            <form><tabs><tab name="general">
              <labels><label description="General" languagecode="1033" /></labels>
              <columns><column width="100%"><sections><section name="s">
                <labels><label description="Sec" languagecode="1033" /></labels>
                <rows>
                  <row><cell><control datafieldname="name" /></cell></row>
                  <row><cell><control id="Dup" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}"><parameters><TargetEntityType>contact</TargetEntityType></parameters></control></cell></row>
                  <row><cell><control id="Dup" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}"><parameters><TargetEntityType>lead</TargetEntityType></parameters></control></cell></row>
                </rows>
              </section></sections></column></columns>
            </tab></tabs></form>
            """;

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(xml), entity), "account.info.form.ts");

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(ts, "Dup: \\{").Count);
    }

    [Fact]
    public void BigInt_Bounds_Are_Not_Corrupted_By_Double_Rounding()
    {
        // Regression: long bounds must not go through double (long.MaxValue rounds past 2^53 and overflowed to a
        // negative max).
        EntityMetadata entity = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .BigInt("bignum", "BigNum", min: long.MinValue, max: long.MaxValue).Display("Big Num")
            .Build();

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "bignum")), entity), "account.info.form.ts");

        Assert.Contains("min: -9223372036854775808,", ts);
        Assert.Contains("max: 9223372036854775807,", ts);
    }

    [Fact]
    public void Field_Placed_In_Two_Sections_Is_Listed_Only_Where_Placed()
    {
        EntityMetadata entity = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("name", "Name").Display("Name")
            .Build();

        // `name` appears in both s1 and s2; it is placed once (s1) with a single `at`, and must be listed only there.
        const string xml = """
            <form><tabs><tab name="general">
              <labels><label description="General" languagecode="1033" /></labels>
              <columns><column width="100%"><sections>
                <section name="s1"><labels><label description="One" languagecode="1033" /></labels>
                  <rows><row><cell><control datafieldname="name" /></cell></row></rows></section>
                <section name="s2"><labels><label description="Two" languagecode="1033" /></labels>
                  <rows><row><cell><control datafieldname="name" /></cell></row></rows></section>
              </sections></column></columns>
            </tab></tabs></form>
            """;

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(xml), entity), "account.info.form.ts");

        Assert.Contains("at: { tab: 'general', section: 's1' },", ts);
        Assert.Contains("s1: {", ts);
        Assert.DoesNotContain("s2: {", ts);          // the empty second section is not resurrected
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(ts, "'name'").Count); // listed once, under s1
    }

    [Fact]
    public void Owner_Lookup_Maps_To_Lookup_Kind()
    {
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .Owner("ownerid", "OwnerId", "systemuser", "team").Display("Owner")
            .Build();

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "ownerid")), entity), "contact.info.form.ts");

        Assert.Contains("kind: 'lookup',", ts);
        Assert.Contains("targets: ['systemuser', 'team'],", ts);
    }

    [Fact]
    public void Fractional_Number_Facets_Keep_Their_Decimals()
    {
        EntityMetadata entity = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .Decimal("ratio", "Ratio", min: 0.5m, max: 2.5m, precision: 2).Display("Ratio")
            .Build();

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "ratio")), entity), "account.info.form.ts");

        Assert.Contains("min: 0.5,", ts);
        Assert.Contains("max: 2.5,", ts);
    }

    [Fact]
    public void Unknown_Field_Cell_Is_Dropped()
    {
        // A control bound to a column absent from the metadata can't be typed, so it's skipped (not emitted).
        EntityMetadata entity = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .String("firstname", "FirstName").Display("First Name")
            .Build();

        string ts = Generate(new FormSource("id", "Main", "Info", FormXmlParser.Parse(OneSection("s", "firstname", "ghostfield")), entity), "contact.info.form.ts");

        Assert.Contains("firstname: {", ts);
        Assert.DoesNotContain("ghostfield", ts);
    }

    [Fact]
    public void Form_Fixtures_Are_Schema_Valid()
    {
        // The golden inputs are real FormXML: both validate against the official Microsoft FormXml.xsd.
        Assert.True(FormXmlSchema.IsValid(ContactFormXml), string.Join("\n", FormXmlSchema.Validate(ContactFormXml)));
        Assert.True(FormXmlSchema.IsValid(AccountFormXml), string.Join("\n", FormXmlSchema.Validate(AccountFormXml)));
    }

    [Fact]
    public void Contact_Form_Matches_Golden()
    {
        const string expected = """
            // generated, do not edit
            import type { FormModel } from 'vela-client';
            import { contact_gendercode, contact_statuscode } from './option-sets';

            export const ContactInformation = {
              entity: 'contact',
              formId: '7c1e3f2a-0000-0000-0000-000000000001',
              formType: 'Main',
              fields: {
                firstname: {
                  kind: 'string',
                  label: 'First Name',
                  required: 'none',
                  at: { tab: 'general', section: 'contactInfo' },
                },
                lastname: {
                  kind: 'string',
                  label: 'Last Name',
                  required: 'required',
                  at: { tab: 'general', section: 'contactInfo' },
                },
                emailaddress1: {
                  kind: 'string',
                  label: 'Email',
                  required: 'none',
                  at: { tab: 'general', section: 'contactInfo' },
                },
                birthdate: {
                  kind: 'date',
                  label: 'Birthday',
                  required: 'none',
                  at: { tab: 'general', section: 'contactInfo' },
                },
                gendercode: {
                  kind: 'choice',
                  label: 'Gender',
                  required: 'none',
                  options: contact_gendercode,
                  at: { tab: 'general', section: 'contactInfo' },
                },
                statuscode: {
                  kind: 'choice',
                  label: 'Status',
                  required: 'required',
                  options: contact_statuscode,
                  at: { tab: 'general', section: 'contactInfo' },
                },
                donotemail: {
                  kind: 'boolean',
                  label: 'Do Not Email',
                  required: 'none',
                  at: { tab: 'general', section: 'contactInfo' },
                },
                parentcustomerid: {
                  kind: 'lookup',
                  label: 'Company',
                  required: 'none',
                  targets: ['account', 'contact'],
                  at: { tab: 'general', section: 'contactInfo' },
                },
                creditlimit: {
                  kind: 'money',
                  label: 'Credit Limit',
                  required: 'none',
                  at: { tab: 'details', section: 'finance' },
                },
              },
              layout: {
                tabs: {
                  general: {
                    label: 'General',
                    sections: {
                      contactInfo: {
                        label: 'Contact',
                        fields: [
                          'firstname',
                          'lastname',
                          'emailaddress1',
                          'birthdate',
                          'gendercode',
                          'statuscode',
                          'donotemail',
                          'parentcustomerid',
                        ],
                      },
                    },
                  },
                  details: {
                    label: 'Details',
                    sections: {
                      finance: {
                        label: 'Finance',
                        fields: ['creditlimit'],
                      },
                    },
                  },
                },
              },
              subgrids: {
                Opportunities: { entity: 'opportunity', relationship: 'opportunity_parent_contact' },
              },
            } as const satisfies FormModel;

            """;

        Assert.Equal(expected, Generate(ContactForm(), "contact.information.form.ts"));
    }

    [Fact]
    public void Account_Form_Matches_Golden()
    {
        const string expected = """
            // generated, do not edit
            import type { FormModel } from 'vela-client';
            import { account_industrycode, account_statuscode, account_new_servicetypes } from './option-sets';

            export const AccountMain = {
              entity: 'account',
              formId: '7c1e3f2a-0000-0000-0000-000000000002',
              formType: 'Main',
              fields: {
                name: {
                  kind: 'string',
                  label: 'Account Name',
                  required: 'required',
                  maxLength: 160,
                  at: { tab: 'general', section: 'accountInfo' },
                },
                accountnumber: {
                  kind: 'string',
                  label: 'Account Number',
                  required: 'none',
                  maxLength: 20,
                  at: { tab: 'general', section: 'accountInfo' },
                },
                description: {
                  kind: 'memo',
                  label: 'Description',
                  required: 'none',
                  maxLength: 2000,
                  format: 'textarea',
                  at: { tab: 'general', section: 'accountInfo' },
                },
                new_establisheddate: {
                  kind: 'date',
                  label: 'Established On',
                  required: 'none',
                  format: 'dateonly',
                  at: { tab: 'general', section: 'accountInfo' },
                },
                lastonholdtime: {
                  kind: 'date',
                  label: 'Last On Hold Time',
                  required: 'none',
                  format: 'datetime',
                  at: { tab: 'general', section: 'accountInfo' },
                },
                industrycode: {
                  kind: 'choice',
                  label: 'Industry',
                  required: 'none',
                  options: account_industrycode,
                  at: { tab: 'general', section: 'accountInfo' },
                },
                statuscode: {
                  kind: 'choice',
                  label: 'Status Reason',
                  required: 'required',
                  options: account_statuscode,
                  at: { tab: 'general', section: 'accountInfo' },
                },
                primarycontactid: {
                  kind: 'lookup',
                  label: 'Primary Contact',
                  required: 'none',
                  targets: ['contact', 'account'],
                  at: { tab: 'general', section: 'accountInfo' },
                },
                entityimage: {
                  kind: 'image',
                  label: 'Logo',
                  required: 'none',
                  at: { tab: 'general', section: 'accountInfo' },
                },
                donotpostalmail: {
                  kind: 'boolean',
                  label: 'Do Not Send Mail',
                  required: 'none',
                  at: { tab: 'general', section: 'preferences' },
                },
                new_servicetypes: {
                  kind: 'multichoice',
                  label: 'Service Types',
                  required: 'none',
                  options: account_new_servicetypes,
                  at: { tab: 'general', section: 'preferences' },
                },
                new_projectteam: {
                  kind: 'lookup',
                  label: 'Project Team',
                  required: 'none',
                  targets: ['systemuser', 'team'],
                  partyList: true,
                  at: { tab: 'general', section: 'preferences' },
                },
                numberofemployees: {
                  kind: 'integer',
                  label: 'Employees',
                  required: 'none',
                  min: 0,
                  max: 1000000,
                  at: { tab: 'details', section: 'demographics' },
                },
                new_globalrank: {
                  kind: 'bigint',
                  label: 'Global Rank',
                  required: 'none',
                  min: 0,
                  at: { tab: 'details', section: 'demographics' },
                },
                new_conversionrate: {
                  kind: 'decimal',
                  label: 'Conversion Rate',
                  required: 'none',
                  min: 0,
                  max: 1,
                  precision: 4,
                  at: { tab: 'details', section: 'demographics' },
                },
                new_creditscore: {
                  kind: 'double',
                  label: 'Credit Score',
                  required: 'none',
                  precision: 2,
                  at: { tab: 'details', section: 'demographics' },
                },
                revenue: {
                  kind: 'money',
                  label: 'Annual Revenue',
                  required: 'none',
                  min: 0,
                  precision: 2,
                  at: { tab: 'details', section: 'finance' },
                },
                accountid: {
                  kind: 'guid',
                  label: 'Account',
                  required: 'none',
                  at: { tab: 'details', section: 'finance' },
                },
                new_contractfile: {
                  kind: 'file',
                  label: 'Signed Contract',
                  required: 'none',
                  at: { tab: 'details', section: 'finance' },
                },
              },
              layout: {
                tabs: {
                  general: {
                    label: 'General',
                    sections: {
                      accountInfo: {
                        label: 'Account Information',
                        fields: [
                          'name',
                          'accountnumber',
                          'description',
                          'new_establisheddate',
                          'lastonholdtime',
                          'industrycode',
                          'statuscode',
                          'primarycontactid',
                          'entityimage',
                        ],
                      },
                      preferences: {
                        label: 'Preferences',
                        fields: [
                          'donotpostalmail',
                          'new_servicetypes',
                          'new_projectteam',
                        ],
                      },
                    },
                  },
                  details: {
                    label: 'Details',
                    sections: {
                      demographics: {
                        label: 'Demographics',
                        fields: [
                          'numberofemployees',
                          'new_globalrank',
                          'new_conversionrate',
                          'new_creditscore',
                        ],
                      },
                      finance: {
                        label: 'Finance',
                        fields: [
                          'revenue',
                          'accountid',
                          'new_contractfile',
                        ],
                      },
                    },
                  },
                },
              },
              subgrids: {
                Contacts: { entity: 'contact', relationship: 'contact_customer_accounts', viewId: '00000000-0000-0000-0000-0000000000c1' },
                Cases: { entity: 'incident', relationship: 'incident_customer_accounts' },
              },
            } as const satisfies FormModel;

            """;

        Assert.Equal(expected, Generate(AccountForm(), "account.main.form.ts"));
    }

    [Fact]
    public void Option_Sets_File_Merges_Both_Forms_In_Reference_Order()
    {
        const string expected = """
            // generated, do not edit

            export enum contact_gendercode {
              Male = 1,
              Female = 2,
            }

            export enum contact_statuscode {
              Active = 1,
              Inactive = 2,
            }

            export enum account_industrycode {
              Accounting = 1,
              Consulting = 2,
              Manufacturing = 3,
              Retail = 4,
              Technology = 5,
            }

            export enum account_statuscode {
              Active = 1,
              Inactive = 2,
            }

            export enum account_new_servicetypes {
              Consulting = 1,
              Support = 2,
              Training = 3,
              Managed = 4,
            }

            """;

        string optionSets = FormModelTsGenerator.Generate(new[] { ContactForm(), AccountForm() })
            .Single(m => m.FileName == "option-sets.ts").Code;

        Assert.Equal(expected, optionSets);
    }

    [Fact]
    public void Generates_One_Form_File_Per_Source_Plus_Shared_Option_Sets()
    {
        string[] files = FormModelTsGenerator.Generate(new[] { ContactForm(), AccountForm() })
            .Select(m => m.FileName)
            .ToArray();

        Assert.Equal(
            new[] { "contact.information.form.ts", "account.main.form.ts", "option-sets.ts" },
            files);
    }

    [Fact]
    public void Form_Without_Option_Sets_Emits_No_Option_Sets_File()
    {
        EntityMetadata entity = MetadataFactory.Entity("task", "Task")
            .PrimaryId("activityid", "ActivityId")
            .String("subject", "Subject").Display("Subject")
            .Build();

        const string xml = """
            <form><tabs><tab name="general">
              <labels><label description="General" languagecode="1033" /></labels>
              <columns><column width="100%"><sections><section name="s">
                <labels><label description="General" languagecode="1033" /></labels>
                <rows><row><cell><control datafieldname="subject" /></cell></row></rows>
              </section></sections></column></columns>
            </tab></tabs></form>
            """;

        var files = FormModelTsGenerator.Generate(new[]
        {
            new FormSource("00000000-0000-0000-0000-0000000000ff", "Main", "Task", FormXmlParser.Parse(xml), entity),
        });

        Assert.DoesNotContain(files, f => f.FileName == "option-sets.ts");
        Assert.Single(files);
    }
}
