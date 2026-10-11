using System;
using Microsoft.Xrm.Sdk;
using Vela.Querying;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers EntityCloneExtensions.Clone<T>(): a create-ready duplicate that drops every link to the source row
// (Id, state, concurrency, primary key, audit columns) and deep-copies the remaining attribute values.
public sealed class EntityCloneExtensionsTests
{
    [Fact]
    public void Clone_drops_the_id_and_the_primary_key_attribute()
    {
        Guid id = Guid.NewGuid();
        TestAccount source = new TestAccount { Id = id, AccountId = id, Name = "Contoso" };

        TestAccount clone = source.Clone();

        Assert.Equal(Guid.Empty, clone.Id);
        Assert.False(clone.Contains("accountid"));
        Assert.Equal("Contoso", clone.Name);
        Assert.Equal("account", clone.LogicalName);
    }

    [Fact]
    public void Clone_drops_concurrency_and_identity_state()
    {
        TestAccount source = new TestAccount { Id = Guid.NewGuid(), Name = "A" };
        source.RowVersion = "42";
        source.EntityState = EntityState.Changed;
        source.KeyAttributes["code"] = "X1";
        source.FormattedValues["name"] = "A (fmt)";

        TestAccount clone = source.Clone();

        Assert.Null(clone.RowVersion);
        Assert.Null(clone.EntityState);
        Assert.Empty(clone.KeyAttributes);
        Assert.Empty(clone.FormattedValues);
    }

    [Fact]
    public void Clone_drops_system_audit_columns_but_keeps_business_columns()
    {
        TestAccount source = new TestAccount { Id = Guid.NewGuid(), Name = "A" };
        source["createdon"] = DateTime.UtcNow;
        source["modifiedby"] = new EntityReference("systemuser", Guid.NewGuid());
        source["versionnumber"] = 7L;
        source["ownerid"] = new EntityReference("systemuser", Guid.NewGuid());
        source["statecode"] = new OptionSetValue(0);

        TestAccount clone = source.Clone();

        Assert.False(clone.Contains("createdon"));
        Assert.False(clone.Contains("modifiedby"));
        Assert.False(clone.Contains("versionnumber"));
        Assert.True(clone.Contains("ownerid"));
        Assert.True(clone.Contains("statecode"));
    }

    [Fact]
    public void Clone_deep_copies_mutable_values_so_the_original_is_untouched()
    {
        EntityReference primary = new EntityReference("contact", Guid.NewGuid()) { Name = "P" };
        TestAccount source = new TestAccount { Id = Guid.NewGuid(), Name = "A" };
        source["primarycontactid"] = primary;
        source["revenue"] = new Money(100m);
        source["industrycode"] = new OptionSetValue(1);
        byte[] bytes = { 1, 2, 3 };
        source["entityimage"] = bytes;

        TestAccount clone = source.Clone();

        // Distinct instances (no aliasing), equal content.
        Assert.NotSame(primary, clone["primarycontactid"]);
        Assert.Equal(primary.Id, ((EntityReference)clone["primarycontactid"]).Id);
        Assert.NotSame(source["revenue"], clone["revenue"]);
        Assert.Equal(100m, ((Money)clone["revenue"]).Value);
        Assert.NotSame(bytes, clone["entityimage"]);
        Assert.Equal(bytes, (byte[])clone["entityimage"]);

        // Mutating the clone's byte copy does not touch the source array.
        ((byte[])clone["entityimage"])[0] = 9;
        Assert.Equal(1, bytes[0]);
    }

    [Fact]
    public void Clone_deep_copies_a_partylist_collection_and_makes_children_create_ready()
    {
        Guid partyId = Guid.NewGuid();
        Entity party = new Entity("activityparty", partyId);
        party["activitypartyid"] = partyId;   // the nested record's own primary key
        party["createdon"] = DateTime.UtcNow;  // a nested audit column
        party["partyid"] = new EntityReference("contact", Guid.NewGuid());
        TestAccount source = new TestAccount { Id = Guid.NewGuid(), Name = "A" };
        source.Customers = new EntityCollection(new[] { party }) { EntityName = "activityparty" };

        TestAccount clone = source.Clone();

        EntityCollection clonedCustomers = clone.Customers;
        Assert.NotSame(source.Customers, clonedCustomers);
        Entity clonedParty = Assert.Single(clonedCustomers.Entities);
        Assert.NotSame(party, clonedParty);
        Assert.Equal(Guid.Empty, clonedParty.Id);
        // Nested records go through the same create-ready copy as the root: own key + audit columns dropped.
        Assert.False(clonedParty.Contains("activitypartyid"));
        Assert.False(clonedParty.Contains("createdon"));
        Assert.True(clonedParty.Contains("partyid"));
    }

    [Fact]
    public void Clone_drops_the_primary_key_of_an_activity_table_that_breaks_the_naming_convention()
    {
        // An email's primary key is "activityid", not "emailid"; a retrieved record carries it with the same
        // value as Entity.Id, so the clone must still drop it to stay create-ready.
        Guid id = Guid.NewGuid();
        Entity email = new Entity("email", id);
        email["activityid"] = id;
        email["subject"] = "Hi";

        Entity clone = email.Clone();

        Assert.Equal(Guid.Empty, clone.Id);
        Assert.False(clone.Contains("activityid"));
        Assert.Equal("Hi", clone["subject"]);
    }

    [Fact]
    public void Clone_deep_copies_an_aliased_value_and_its_wrapped_value()
    {
        TestAccount source = new TestAccount { Id = Guid.NewGuid(), Name = "A" };
        Money wrapped = new Money(50m);
        source["contact.revenue"] = new AliasedValue("contact", "revenue", wrapped);

        TestAccount clone = source.Clone();

        AliasedValue clonedAlias = Assert.IsType<AliasedValue>(clone["contact.revenue"]);
        Assert.Equal("contact", clonedAlias.EntityLogicalName);
        Assert.Equal("revenue", clonedAlias.AttributeLogicalName);
        Money clonedMoney = Assert.IsType<Money>(clonedAlias.Value);
        Assert.NotSame(wrapped, clonedMoney);
        Assert.Equal(50m, clonedMoney.Value);
    }

    [Fact]
    public void Clone_preserves_the_logical_name_of_a_late_bound_entity()
    {
        Entity source = new Entity("account", Guid.NewGuid());
        source["name"] = "Late";

        Entity clone = source.Clone();

        Assert.Equal("account", clone.LogicalName);
        Assert.Equal(Guid.Empty, clone.Id);
        Assert.Equal("Late", clone["name"]);
        Assert.False(clone.Contains("accountid"));
    }

    [Fact]
    public void Clone_rejects_a_null_source()
    {
        TestAccount? source = null;
        Assert.Throws<ArgumentNullException>(() => source!.Clone());
    }

    [Fact]
    public void Clone_keeps_a_guid_column_that_only_happens_to_equal_the_id()
    {
        // A self-reference is business data, not the primary key: only the key column itself is dropped.
        Guid id = Guid.NewGuid();
        Entity source = new Entity("account", id);
        source["accountid"] = id;
        source["new_originalrecordid"] = id;

        Entity clone = source.Clone();

        Assert.False(clone.Contains("accountid"));
        Assert.Equal(id, clone["new_originalrecordid"]);
    }

    [Fact]
    public void Clone_drops_the_primary_key_a_model_names_on_its_Id_property()
    {
        Guid id = Guid.NewGuid();
        OddKeyRecord source = new OddKeyRecord { Id = id };
        source["new_oddkey"] = id;
        source["new_name"] = "A";

        OddKeyRecord clone = source.Clone();

        Assert.False(clone.Contains("new_oddkey"));
        Assert.Equal("A", clone["new_name"]);
    }

    [Microsoft.Xrm.Sdk.Client.EntityLogicalName("new_record")]
    public sealed class OddKeyRecord : Entity
    {
        public OddKeyRecord()
            : base("new_record")
        {
        }

        [AttributeLogicalName("new_oddkey")]
        public override Guid Id
        {
            get => base.Id;
            set => base.Id = value;
        }
    }
}
