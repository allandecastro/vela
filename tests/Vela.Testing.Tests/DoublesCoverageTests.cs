using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Covers the small public test doubles whose branches the integration suites don't exercise:
    // HookOrganizationService (hook-set vs default for every method), RecordingTracingService (format/args
    // paths) and FakeEntityMetadata (the CreateAttribute type switch and the SetSealed/Last guards).
    public sealed class DoublesCoverageTests
    {
        // ---- HookOrganizationService -------------------------------------------------------------------

        [Fact]
        public void Hook_service_throws_naming_the_hook_when_it_is_not_set()
        {
            HookOrganizationService svc = new HookOrganizationService();

            // An unset hook used to return null / Guid.Empty / nothing, so the plugin failed later with a
            // NullReferenceException far from the call. Each call now fails at once and names the hook to set.
            AssertUnset("OnCreate", () => svc.Create(new Entity("account")));
            AssertUnset("OnRetrieve", () => svc.Retrieve("account", Guid.NewGuid(), new ColumnSet()));
            AssertUnset("OnExecute", () => svc.Execute(new OrganizationRequest("WhoAmI")));
            AssertUnset("OnRetrieveMultiple", () => svc.RetrieveMultiple(new QueryExpression("account")));
            AssertUnset("OnUpdate", () => svc.Update(new Entity("account")));
            AssertUnset("OnDelete", () => svc.Delete("account", Guid.NewGuid()));
            AssertUnset("OnAssociate", () => svc.Associate("account", Guid.NewGuid(), new Relationship("r"), new EntityReferenceCollection()));
            AssertUnset("OnDisassociate", () => svc.Disassociate("account", Guid.NewGuid(), new Relationship("r"), new EntityReferenceCollection()));
        }

        private static void AssertUnset(string hook, Action call)
        {
            NotSupportedException ex = Assert.Throws<NotSupportedException>(call);
            Assert.Contains("HookOrganizationService." + hook + " is not set", ex.Message);
        }

        [Fact]
        public void Hook_service_invokes_each_configured_hook()
        {
            Guid created = Guid.NewGuid();
            Entity retrieved = new Entity("account") { Id = created };
            OrganizationResponse response = new OrganizationResponse();
            EntityCollection collection = new EntityCollection();
            Entity? updated = null;
            string? deleted = null;
            string? associated = null;
            string? disassociated = null;

            HookOrganizationService svc = new HookOrganizationService
            {
                OnCreate = _ => created,
                OnRetrieve = (name, id, cols) => retrieved,
                OnExecute = _ => response,
                OnRetrieveMultiple = _ => collection,
                OnUpdate = e => updated = e,
                OnDelete = (name, id) => deleted = name,
                OnAssociate = (name, id, rel, rel2) => associated = name,
                OnDisassociate = (name, id, rel, rel2) => disassociated = name,
            };

            Assert.Equal(created, svc.Create(new Entity("account")));
            Assert.Same(retrieved, svc.Retrieve("account", created, new ColumnSet()));
            Assert.Same(response, svc.Execute(new OrganizationRequest("WhoAmI")));
            Assert.Same(collection, svc.RetrieveMultiple(new QueryExpression("account")));

            Entity toUpdate = new Entity("account");
            svc.Update(toUpdate);
            svc.Delete("contact", Guid.NewGuid());
            svc.Associate("account", Guid.NewGuid(), new Relationship("r"), new EntityReferenceCollection());
            svc.Disassociate("account", Guid.NewGuid(), new Relationship("r"), new EntityReferenceCollection());

            Assert.Same(toUpdate, updated);
            Assert.Equal("contact", deleted);
            Assert.Equal("account", associated);
            Assert.Equal("account", disassociated);
        }

        // ---- RecordingTracingService -------------------------------------------------------------------

        [Fact]
        public void Tracing_records_plain_and_formatted_lines_and_ignores_empty()
        {
            RecordingTracingService trace = new RecordingTracingService();

            trace.Trace("");                       // empty -> ignored
            trace.Trace(null!);                    // null -> ignored
            trace.Trace("plain message");          // no args -> verbatim
            trace.Trace("hello {0}", "world");     // args -> string.Format

            Assert.Equal(2, trace.Traces.Count);
            Assert.Equal("plain message", trace.Traces[0]);
            Assert.Equal("hello world", trace.Traces[1]);
            Assert.Equal("plain message" + Environment.NewLine + "hello world", trace.Output);

            trace.Clear();
            Assert.Empty(trace.Traces);
        }

        // ---- FakeEntityMetadata ------------------------------------------------------------------------

        [Theory]
        [InlineData(AttributeTypeCode.Picklist, typeof(PicklistAttributeMetadata))]
        [InlineData(AttributeTypeCode.State, typeof(PicklistAttributeMetadata))]
        [InlineData(AttributeTypeCode.Status, typeof(PicklistAttributeMetadata))]
        [InlineData(AttributeTypeCode.Integer, typeof(IntegerAttributeMetadata))]
        [InlineData(AttributeTypeCode.BigInt, typeof(BigIntAttributeMetadata))]
        [InlineData(AttributeTypeCode.Boolean, typeof(BooleanAttributeMetadata))]
        [InlineData(AttributeTypeCode.DateTime, typeof(DateTimeAttributeMetadata))]
        [InlineData(AttributeTypeCode.Decimal, typeof(DecimalAttributeMetadata))]
        [InlineData(AttributeTypeCode.Double, typeof(DoubleAttributeMetadata))]
        [InlineData(AttributeTypeCode.Money, typeof(MoneyAttributeMetadata))]
        [InlineData(AttributeTypeCode.Lookup, typeof(LookupAttributeMetadata))]
        [InlineData(AttributeTypeCode.Customer, typeof(LookupAttributeMetadata))]
        [InlineData(AttributeTypeCode.Owner, typeof(LookupAttributeMetadata))]
        [InlineData(AttributeTypeCode.Uniqueidentifier, typeof(UniqueIdentifierAttributeMetadata))]
        [InlineData(AttributeTypeCode.Memo, typeof(MemoAttributeMetadata))]
        [InlineData(AttributeTypeCode.String, typeof(StringAttributeMetadata))]
        public void CreateAttribute_maps_each_type_code_to_its_metadata_class(AttributeTypeCode type, Type expected)
        {
            EntityMetadata metadata = FakeEntityMetadata.For("table").Attribute("attr", type).Build();

            AttributeMetadata attribute = Assert.Single(metadata.Attributes);
            Assert.IsType(expected, attribute);
            Assert.Equal("attr", attribute.LogicalName);
            Assert.Equal(type, attribute.AttributeType);
        }

        [Fact]
        public void Build_carries_entity_level_and_attribute_level_configuration()
        {
            EntityMetadata metadata = FakeEntityMetadata.For("new_widget")
                .Schema("new_Widget")
                .Ownership(OwnershipTypes.OrganizationOwned)
                .PrimaryId("new_widgetid")
                .PrimaryName("new_name").MaxLength(200).DisplayName("Name").Description("The name").Required(AttributeRequiredLevel.ApplicationRequired)
                .Attribute("new_count", AttributeTypeCode.Integer).Range(0, 10)
                .Attribute("new_amount", AttributeTypeCode.Decimal).Precision(2).Range(0m, 99m)
                .Attribute("new_owner", AttributeTypeCode.Lookup).Targets("systemuser", "team")
                .Picklist("new_color", (1, "Red"), (2, "Green"))
                .ManyToMany("new_widget_tag", "new_widget_tag", "new_widget", "new_widgetid", "tag", "tagid")
                .Build();

            Assert.Equal("new_widget", metadata.LogicalName);
            Assert.Equal("new_Widget", metadata.SchemaName);
            Assert.Equal(OwnershipTypes.OrganizationOwned, metadata.OwnershipType);
            Assert.Equal("new_widgetid", metadata.PrimaryIdAttribute);
            Assert.Equal("new_name", metadata.PrimaryNameAttribute);
            Assert.Equal(6, metadata.Attributes.Length); // id, name, count, amount, owner, color
            Assert.Single(metadata.ManyToManyRelationships);
        }

        [Fact]
        public void Configuring_before_adding_an_attribute_throws()
        {
            Assert.Throws<InvalidOperationException>(() => FakeEntityMetadata.For("table").DisplayName("oops"));
        }

        [Fact]
        public void Setting_a_property_the_attribute_type_lacks_throws()
        {
            // MaxLength targets String/Memo; an Integer attribute has no MaxLength setter, so SetSealed fails.
            Assert.Throws<InvalidOperationException>(
                () => FakeEntityMetadata.For("table").Attribute("num", AttributeTypeCode.Integer).MaxLength(10));
        }
    }
}
