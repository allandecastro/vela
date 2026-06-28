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

    private static string GenerateAccount(bool dataAnnotations = true) =>
        ModelGenerator.Generate(new[] { Account() }, new ModelGenOptions("Acme.Shared.Models.Generated", dataAnnotations))
            .Single().Code;

    [Fact]
    public void Generates_PartialClass_WithVelaConventions()
    {
        GeneratedModel model = ModelGenerator.Generate(new[] { Account() }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single();

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

        string code = ModelGenerator.Generate(new[] { md }, new ModelGenOptions("Acme.Shared.Models.Generated")).Single().Code;

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

        Assert.Contains("public static EntityReference ByAccountNumberKey(params object[] keyValues)", code);
        Assert.Contains("KeyAttributeCollection keys = new KeyAttributeCollection();", code);
        Assert.Contains("\"accountnumber\"", code);
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
}
