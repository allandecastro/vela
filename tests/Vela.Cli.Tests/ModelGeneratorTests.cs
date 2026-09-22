using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

public class ModelGeneratorTests
{
    private static EntityMetadata Account() =>
        MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("name", "Name", maxLength: 160).Display("Account Name").Describe("The company name")
            .Money("revenue", "Revenue", min: 0m, max: 1000000000m)
            .Lookup("primarycontactid", "PrimaryContactId", "contact")
            .Build();

    // Account carries a lookup, so generation also emits the LookupTargetsAttribute support file — select the table.
    private static string GenerateAccount(bool dataAnnotations = true) =>
        ModelGenerator.Generate(new[] { Account() }, new ModelGenOptions("Acme.Shared.Models.Generated", dataAnnotations))
            .Single(m => m.FileName == "Account.cs").Code;

    [Fact]
    public void Generates_PartialClass_WithVelaConventions()
    {
        GeneratedModel model = ModelGenerator.Generate(new[] { Account() }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single(m => m.FileName == "Account.cs");

        Assert.Equal("Account.cs", model.FileName);
        Assert.Contains("namespace Acme.Shared.Models.Generated", model.Code);
        Assert.Contains("public partial class Account : Entity", model.Code);
        Assert.Contains("public const string EntityLogicalNameValue = \"account\";", model.Code);
    }

    [Fact]
    public void PrimaryId_IsSyncedWithEntityId()
    {
        string code = GenerateAccount();

        Assert.Contains("public override Guid Id", code);
        Assert.Contains("SetAttributeValue(\"accountid\", value)", code);
        Assert.Contains("public Guid? AccountId", code);
    }

    [Fact]
    public void Property_IsTyped_WithLogicalNameAttribute()
    {
        string code = GenerateAccount();

        Assert.Contains("[AttributeLogicalName(\"name\")]", code);
        Assert.Contains("public string Name", code);
        Assert.Contains("public Money Revenue", code);
        Assert.Contains("public EntityReference PrimaryContactId", code);
    }

    [Fact]
    public void Virtual_companion_name_attributes_are_skipped()
    {
        // The "<name>name" formatted-name pseudo-columns (AttributeOf set) of option sets and lookups are noise —
        // the generator must skip them and keep only the real columns.
        EntityMetadata entity = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("name", "Name", maxLength: 160)
            .String("primarycontactidname", "PrimaryContactIdName", maxLength: 100).Companion("primarycontactid")
            .Build();

        string code = ModelGenerator.Generate(new[] { entity }, new ModelGenOptions("Acme.Shared.Models.Generated"))
            .Single(m => m.FileName == "Account.cs").Code;

        Assert.Contains("public string Name", code);            // real column kept
        Assert.DoesNotContain("primarycontactidname", code);    // companion skipped
        Assert.DoesNotContain("PrimaryContactIdName", code);
    }

    [Fact]
    public void Multiline_descriptions_do_not_break_generated_doc_comments()
    {
        // Dataverse descriptions are routinely multi-line; a raw CR/LF inside a single-line /// <summary> leaves the
        // continuation without a /// prefix and produces uncompilable generated code.
        EntityMetadata entity = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("name", "Name", maxLength: 160).Display("Account Name").Describe("First line\r\nSecond line")
            .Build();

        string code = ModelGenerator.Generate(new[] { entity }, new ModelGenOptions("Acme.Shared.Models.Generated"))
            .Single(m => m.FileName == "Account.cs").Code;

        Assert.DoesNotContain("First line\r", code); // no raw CR/LF survives inside the doc comment
        Assert.DoesNotContain("First line\n", code);
        Assert.Contains("First line Second line", code); // collapsed to a single line
    }

    [Fact]
    public void PartyList_IsTyped_AsEntityCollection()
    {
        // ActivityParty columns (email From/To/Cc) are partylists -> EntityCollection at runtime.
        EntityMetadata md = MetadataFactory.Entity("email", "Email")
            .PrimaryId("activityid", "ActivityId")
            .PartyList("to", "To")
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single().Code;

        Assert.Contains("public EntityCollection To", code);
        Assert.Contains("GetAttributeValue<EntityCollection>(\"to\")", code);
    }

    [Fact]
    public void FileColumn_IsTyped_AsNullableGuid()
    {
        // A File column round-trips its fileid (a Guid), not the file content.
        EntityMetadata md = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .File("vela_document", "VelaDocument")
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single().Code;

        Assert.Contains("public Guid? VelaDocument", code);
        Assert.Contains("GetAttributeValue<Guid?>(\"vela_document\")", code);
    }

    [Fact]
    public void ImageColumn_IsTyped_AsByteArray()
    {
        // An Image column round-trips its thumbnail bytes.
        EntityMetadata md = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .Image("vela_photo", "VelaPhoto")
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single().Code;

        Assert.Contains("public byte[] VelaPhoto", code);
        Assert.Contains("GetAttributeValue<byte[]>(\"vela_photo\")", code);
    }

    [Fact]
    public void PolymorphicCustomerLookup_IsTyped_AsEntityReference()
    {
        // A Customer / polymorphic lookup (account|contact) can't be strong-typed to one table —
        // EntityReference is the correct early-bound type (targets are documented, not encoded in the type).
        EntityMetadata md = MetadataFactory.Entity("opportunity", "Opportunity")
            .PrimaryId("opportunityid", "OpportunityId")
            .Customer("customerid", "CustomerId", "account", "contact")
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single(m => m.FileName == "Opportunity.cs").Code;

        Assert.Contains("public EntityReference CustomerId", code);
    }

    [Fact]
    public void XmlDoc_CarriesDisplayNameDescriptionAndConstraints()
    {
        string code = GenerateAccount();

        Assert.Contains("Account Name — The company name (max 160)", code);
        Assert.Contains("Targets: contact", code);
    }

    [Fact]
    public void DataAnnotations_EmittedByDefault()
    {
        string code = GenerateAccount(dataAnnotations: true);

        Assert.Contains("using System.ComponentModel.DataAnnotations;", code);
        Assert.Contains("[StringLength(160)]", code);
        Assert.Contains("[Range(0, 1000000000)]", code);
    }

    [Fact]
    public void DataAnnotations_OmittedWhenDisabled()
    {
        string code = GenerateAccount(dataAnnotations: false);

        Assert.DoesNotContain("using System.ComponentModel.DataAnnotations;", code);
        Assert.DoesNotContain("[StringLength(", code);
        Assert.DoesNotContain("[Range(", code);
    }

    [Fact]
    public void Generation_IsDeterministic()
    {
        Assert.Equal(GenerateAccount(), GenerateAccount());
    }

    private static System.Collections.Generic.IReadOnlyList<GeneratedModel> GenerateWithChoices() =>
        ModelGenerator.Generate(
            new[]
            {
                MetadataFactory.Entity("account", "Account")
                    .PrimaryId("accountid", "AccountId")
                    .Choice("statuscode", "StatusCode", global: false, null, (1, "Active"), (2, "In Progress!"))
                    .Choice("new_color", "Color", global: true, "new_colorset", (1, "Red"), (2, "Green"))
                    .Build(),
            },
            new ModelGenOptions("Acme.Shared.Models.Generated"));

    [Fact]
    public void AlternateKey_EmitsTypedReferenceFactory()
    {
        EntityMetadata md = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("accountnumber", "AccountNumber")
            .Key("new_accountnumberkey", "AccountNumberKey", "accountnumber")
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single().Code;

        // The key column type flows through to a typed parameter (accountnumber is a string).
        Assert.Contains("public static EntityReference ByAccountNumberKey(string accountNumber)", code);
        Assert.Contains("KeyAttributeCollection keys = new KeyAttributeCollection();", code);
        Assert.Contains("keys[\"accountnumber\"] = accountNumber;", code);
    }

    [Fact]
    public void AlternateKey_LookupColumn_TypedAsGuid()
    {
        // A lookup alternate-key column is keyed on the referenced record's Guid, so the factory takes a Guid.
        EntityMetadata md = MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .Lookup("parentcustomerid", "ParentCustomerId", "account")
            .Key("new_parentkey", "ParentKey", "parentcustomerid")
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Models"))
            .Single(m => m.FileName == "Contact.cs").Code;

        Assert.Contains("public static EntityReference ByParentKey(Guid parentCustomerId)", code);
        Assert.Contains("keys[\"parentcustomerid\"] = parentCustomerId;", code);
    }

    [Fact]
    public void AlternateKey_EmitsOneTypedParameterPerKeyColumn()
    {
        // A composite alternate key becomes one typed parameter per column, in declared order.
        EntityMetadata md = MetadataFactory.Entity("invoiceline", "InvoiceLine")
            .PrimaryId("invoicelineid", "InvoiceLineId")
            .String("code", "Code")
            .Integer("year", "Year")
            .Key("new_codeyearkey", "CodeYearKey", "code", "year")
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single().Code;

        Assert.Contains("public static EntityReference ByCodeYearKey(string code, int year)", code);
        Assert.Contains("keys[\"code\"] = code;", code);
        Assert.Contains("keys[\"year\"] = year;", code);
    }

    [Fact]
    public void MultiSelect_EmitsEnumListProperty()
    {
        EntityMetadata md = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .MultiSelect("new_tags", "Tags", global: false, null, (1, "VIP"), (2, "Partner"))
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single().Code;

        // A list of enum values (not a [Flags] enum) backed by OptionSetValueCollection.
        Assert.Contains("public System.Collections.Generic.IList<TagsOptionSet> Tags", code);
        Assert.Contains("OptionSetValueCollection raw = GetAttributeValue<OptionSetValueCollection>(\"new_tags\")", code);
        Assert.Contains("public enum TagsOptionSet", code);
        Assert.DoesNotContain("[Flags]", code);
    }

    [Fact]
    public void LocalOptionSet_NestedEnum_AndTypedProperty()
    {
        string code = GenerateWithChoices().Single(m => m.FileName == "Account.cs").Code;

        Assert.Contains("public StatusCodeOptionSet? StatusCode", code);
        Assert.Contains("public enum StatusCodeOptionSet", code);
        Assert.Contains("Active = 1,", code);
        // Label sanitized to a valid identifier.
        Assert.Contains("InProgress = 2,", code);
    }

    [Fact]
    public void GlobalOptionSet_StandaloneEnumFile_AndTypedProperty()
    {
        System.Collections.Generic.IReadOnlyList<GeneratedModel> models = GenerateWithChoices();

        // The table property references the shared enum...
        Assert.Contains("public New_colorset? Color", models.Single(m => m.FileName == "Account.cs").Code);

        // ...which is emitted as its own file (not nested in the table).
        GeneratedModel global = models.Single(m => m.FileName == "New_colorset.cs");
        Assert.Contains("public enum New_colorset", global.Code);
        Assert.Contains("Red = 1,", global.Code);
        Assert.Contains("Green = 2,", global.Code);
    }

    [Fact]
    public void RequiredLevel_EmittedWithFullLevel_AndSupportFile()
    {
        // The full four-value level is preserved (not a lossy boolean), and the self-contained attribute is emitted.
        System.Collections.Generic.IReadOnlyList<GeneratedModel> models = ModelGenerator.Generate(
            new[]
            {
                MetadataFactory.Entity("account", "Account")
                    .PrimaryId("accountid", "AccountId")
                    .String("name", "Name", maxLength: 160).Required(AttributeRequiredLevel.ApplicationRequired)
                    .Build(),
            },
            new ModelGenOptions("Acme.Shared.Models.Generated"));

        Assert.Contains("[RequiredLevel(Microsoft.Xrm.Sdk.Metadata.AttributeRequiredLevel.ApplicationRequired)]", models.Single(m => m.FileName == "Account.cs").Code);

        GeneratedModel support = models.Single(m => m.FileName == "RequiredLevelAttribute.cs");
        Assert.Contains("public sealed class RequiredLevelAttribute : Attribute", support.Code);
        Assert.Contains("public AttributeRequiredLevel Level", support.Code);
    }

    [Fact]
    public void RequiredLevel_NotEmittedForNoneLevel()
    {
        // The default level is None -> no attribute, and no support file.
        System.Collections.Generic.IReadOnlyList<GeneratedModel> models = ModelGenerator.Generate(
            new[]
            {
                MetadataFactory.Entity("account", "Account")
                    .PrimaryId("accountid", "AccountId")
                    .String("name", "Name")
                    .Build(),
            },
            new ModelGenOptions("Acme.Shared.Models.Generated"));

        Assert.DoesNotContain("[RequiredLevel", models.Single(m => m.FileName == "Account.cs").Code);
        Assert.DoesNotContain(models, m => m.FileName == "RequiredLevelAttribute.cs");
    }

    [Fact]
    public void LookupTargets_EmittedForLookup_AndSupportFile()
    {
        System.Collections.Generic.IReadOnlyList<GeneratedModel> models =
            ModelGenerator.Generate(new[] { Account() }, new ModelGenOptions("Acme.Shared.Models.Generated"));

        Assert.Contains("[LookupTargets(\"contact\")]", models.Single(m => m.FileName == "Account.cs").Code);

        GeneratedModel support = models.Single(m => m.FileName == "LookupTargetsAttribute.cs");
        Assert.Contains("public sealed class LookupTargetsAttribute : Attribute", support.Code);
        Assert.Contains("public string[] Targets", support.Code);
    }

    [Fact]
    public void LookupTargets_MultiTarget_ForCustomerAndPolymorphicLookups()
    {
        // Customer (and any polymorphic lookup) is simply a lookup with several targets -> no special case.
        EntityMetadata md = MetadataFactory.Entity("opportunity", "Opportunity")
            .PrimaryId("opportunityid", "OpportunityId")
            .Customer("customerid", "CustomerId", "account", "contact")
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated"))
            .Single(m => m.FileName == "Opportunity.cs").Code;

        Assert.Contains("[LookupTargets(\"account\", \"contact\")]", code);
    }

    [Fact]
    public void LookupTargets_NotEmittedForPartyList()
    {
        // A partylist maps to EntityCollection and its real targets live on activityparty.partyid -> stays override-only.
        EntityMetadata md = MetadataFactory.Entity("email", "Email")
            .PrimaryId("activityid", "ActivityId")
            .PartyList("to", "To")
            .Build();

        System.Collections.Generic.IReadOnlyList<GeneratedModel> models =
            ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated"));

        Assert.DoesNotContain("[LookupTargets", models.Single(m => m.FileName == "Email.cs").Code);
        Assert.DoesNotContain(models, m => m.FileName == "LookupTargetsAttribute.cs");
    }

    [Fact]
    public void FacetAttributes_EmittedEvenWithoutDataAnnotations()
    {
        // [RequiredLevel]/[LookupTargets] are not System.ComponentModel annotations, so --no-annotations keeps them.
        EntityMetadata md = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .String("name", "Name", maxLength: 160).Required(AttributeRequiredLevel.SystemRequired)
            .Lookup("primarycontactid", "PrimaryContactId", "contact")
            .Build();

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated", DataAnnotations: false))
            .Single(m => m.FileName == "Account.cs").Code;

        Assert.DoesNotContain("[StringLength(", code);
        Assert.Contains("[RequiredLevel(Microsoft.Xrm.Sdk.Metadata.AttributeRequiredLevel.SystemRequired)]", code);
        Assert.Contains("[LookupTargets(\"contact\")]", code);
    }

    private static EntityMetadata Contact() =>
        MetadataFactory.Entity("contact", "Contact")
            .PrimaryId("contactid", "ContactId")
            .String("firstname", "FirstName", maxLength: 50)
            .Lookup("parentcustomerid", "ParentCustomerId", "account")
            .Build();

    [Fact]
    public void Generates_N1_nav_property_when_target_is_in_the_batch()
    {
        EntityMetadata account = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .Lookup("primarycontactid", "PrimaryContactId", "contact")
            .ManyToOne("account_primary_contact", "primarycontactid", "contact")
            .Build();

        string code = ModelGenerator.Generate(new[] { account, Contact() }, new ModelGenOptions("Acme.Models"))
            .Single(m => m.FileName == "Account.cs").Code;

        // The raw FK stays; a typed nav named after the FK minus "Id" is added, carrying the relationship schema name.
        Assert.Contains("public EntityReference PrimaryContactId", code);
        Assert.Contains("[RelationshipSchemaName(\"account_primary_contact\")]", code);
        Assert.Contains("public Contact? PrimaryContact { get; set; }", code);
    }

    [Fact]
    public void Generates_1N_child_collection_when_child_is_in_the_batch()
    {
        EntityMetadata account = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .OneToMany("contact_customer_accounts", "contact", "parentcustomerid")
            .Build();

        string code = ModelGenerator.Generate(new[] { account, Contact() }, new ModelGenOptions("Acme.Models"))
            .Single(m => m.FileName == "Account.cs").Code;

        Assert.Contains("[RelationshipSchemaName(\"contact_customer_accounts\")]", code);
        Assert.Contains("public System.Collections.Generic.IList<Contact> Contacts { get; set; } = new System.Collections.Generic.List<Contact>();", code);
    }

    [Fact]
    public void Emits_the_RelationshipSchemaName_support_attribute_file_only_when_used()
    {
        EntityMetadata account = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .OneToMany("contact_customer_accounts", "contact", "parentcustomerid")
            .Build();

        var withRel = ModelGenerator.Generate(new[] { account, Contact() }, new ModelGenOptions("Acme.Models"));
        Assert.Contains(withRel, m => m.FileName == "RelationshipSchemaNameAttribute.cs");

        // No relationship metadata -> no support file.
        var withoutRel = ModelGenerator.Generate(new[] { Account() }, new ModelGenOptions("Acme.Models"));
        Assert.DoesNotContain(withoutRel, m => m.FileName == "RelationshipSchemaNameAttribute.cs");
    }

    [Fact]
    public void Skips_a_relationship_whose_target_table_is_not_generated()
    {
        // Account references contact, but only Account is generated -> no Contact class exists, so no nav (the
        // code would not compile). The raw EntityReference still carries the link.
        EntityMetadata account = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .Lookup("primarycontactid", "PrimaryContactId", "contact")
            .ManyToOne("account_primary_contact", "primarycontactid", "contact")
            .OneToMany("contact_customer_accounts", "contact", "parentcustomerid")
            .Build();

        string code = ModelGenerator.Generate(new[] { account }, new ModelGenOptions("Acme.Models"))
            .Single(m => m.FileName == "Account.cs").Code;

        Assert.DoesNotContain("[RelationshipSchemaName(", code);
        Assert.Contains("public EntityReference PrimaryContactId", code);
    }

    [Fact]
    public void Generates_an_NN_collection_marked_ManyToMany()
    {
        EntityMetadata account = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .ManyToMany("account_contacts_nn", "account", "contact")
            .Build();

        string code = ModelGenerator.Generate(new[] { account, Contact() }, new ModelGenOptions("Acme.Models"))
            .Single(m => m.FileName == "Account.cs").Code;

        Assert.Contains("[RelationshipSchemaName(\"account_contacts_nn\", ManyToMany = true)]", code);
        Assert.Contains("public System.Collections.Generic.IList<Contact> Contacts { get; set; } = new System.Collections.Generic.List<Contact>();", code);
    }

    [Fact]
    public void Skips_a_self_referential_NN()
    {
        EntityMetadata account = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .ManyToMany("account_related_accounts", "account", "account")
            .Build();

        string code = ModelGenerator.Generate(new[] { account }, new ModelGenOptions("Acme.Models"))
            .Single(m => m.FileName == "Account.cs").Code;

        Assert.DoesNotContain("ManyToMany = true", code);
    }

    [Fact]
    public void Skips_an_NN_whose_other_table_is_not_generated()
    {
        EntityMetadata account = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .ManyToMany("account_teams_nn", "account", "team")
            .Build();

        string code = ModelGenerator.Generate(new[] { account }, new ModelGenOptions("Acme.Models"))
            .Single(m => m.FileName == "Account.cs").Code;

        Assert.DoesNotContain("ManyToMany = true", code);
    }

    [Fact]
    public void Skips_a_polymorphic_lookup_nav_keeping_the_EntityReference()
    {
        // A Customer/polymorphic lookup shows up as several N:1 relationships on the SAME referencing attribute.
        // There is no single target type, so no typed nav is generated; the EntityReference stays.
        EntityMetadata account = MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId")
            .Customer("customerid", "CustomerId", "account", "contact")
            .ManyToOne("account_customer_accounts", "customerid", "account")
            .ManyToOne("contact_customer_accounts_poly", "customerid", "contact")
            .Build();

        string code = ModelGenerator.Generate(new[] { account, Contact() }, new ModelGenOptions("Acme.Models"))
            .Single(m => m.FileName == "Account.cs").Code;

        Assert.DoesNotContain("[RelationshipSchemaName(", code);
        Assert.Contains("public EntityReference CustomerId", code);
    }
}
