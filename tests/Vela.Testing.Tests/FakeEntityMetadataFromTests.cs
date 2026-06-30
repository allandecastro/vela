using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class FakeEntityMetadataFromTests
    {
        public enum SampleStatus
        {
            Draft = 1,
            Active = 2,
            Closed = 7,
        }

        // Mirrors how `vela models` emits an early-bound table: [EntityLogicalName] on the class, every column a
        // property carrying [AttributeLogicalName], picklists as nullable generated enums, lookups as
        // EntityReference, money as Money, partylists as EntityCollection.
        [EntityLogicalName("vela_order")]
        public sealed class SampleOrder : Entity
        {
            public SampleOrder() : base("vela_order") { }

            [AttributeLogicalName("vela_orderid")]
            public Guid? OrderId
            {
                get => GetAttributeValue<Guid?>("vela_orderid");
                set => SetAttributeValue("vela_orderid", value);
            }

            // [StringLength] is what `vela models` emits for a String column's MaxLength.
            [AttributeLogicalName("name")]
            [StringLength(160)]
            public string Name
            {
                get => GetAttributeValue<string>("name");
                set => SetAttributeValue("name", value);
            }

            // [Range] is what `vela models` emits for a numeric column's MinValue/MaxValue. Whole-number bounds
            // pick the int constructor (Integer here); fractional bounds pick the double one (Weight/Ratio below).
            [AttributeLogicalName("vela_quantity")]
            [Range(1, 1000)]
            public int? Quantity
            {
                get => GetAttributeValue<int?>("vela_quantity");
                set => SetAttributeValue("vela_quantity", value);
            }

            [AttributeLogicalName("vela_total")]
            [Range(0, 1000000)]
            public Money Total
            {
                get => GetAttributeValue<Money>("vela_total");
                set => SetAttributeValue("vela_total", value);
            }

            [AttributeLogicalName("vela_weight")]
            [Range(0.5, 99.5)]
            public decimal? Weight
            {
                get => GetAttributeValue<decimal?>("vela_weight");
                set => SetAttributeValue("vela_weight", value);
            }

            [AttributeLogicalName("vela_ratio")]
            [Range(-1.5, 1.5)]
            public double? Ratio
            {
                get => GetAttributeValue<double?>("vela_ratio");
                set => SetAttributeValue("vela_ratio", value);
            }

            [AttributeLogicalName("vela_isrush")]
            public bool? IsRush
            {
                get => GetAttributeValue<bool?>("vela_isrush");
                set => SetAttributeValue("vela_isrush", value);
            }

            [AttributeLogicalName("vela_due")]
            public DateTime? Due
            {
                get => GetAttributeValue<DateTime?>("vela_due");
                set => SetAttributeValue("vela_due", value);
            }

            [AttributeLogicalName("vela_customerid")]
            public EntityReference CustomerId
            {
                get => GetAttributeValue<EntityReference>("vela_customerid");
                set => SetAttributeValue("vela_customerid", value);
            }

            [AttributeLogicalName("statuscode")]
            public SampleStatus? StatusCode
            {
                get { OptionSetValue raw = GetAttributeValue<OptionSetValue>("statuscode"); return raw == null ? (SampleStatus?)null : (SampleStatus)raw.Value; }
                set => SetAttributeValue("statuscode", value.HasValue ? new OptionSetValue((int)value.Value) : null);
            }

            [AttributeLogicalName("ownerid")]
            public EntityReference OwnerId
            {
                get => GetAttributeValue<EntityReference>("ownerid");
                set => SetAttributeValue("ownerid", value);
            }

            // Partylist — typed as EntityCollection, has no faithful single AttributeTypeCode; must be skipped.
            [AttributeLogicalName("vela_recipients")]
            public EntityCollection Recipients
            {
                get => GetAttributeValue<EntityCollection>("vela_recipients");
                set => SetAttributeValue("vela_recipients", value);
            }
        }

        private static EntityMetadata Derived() => FakeEntityMetadata.From<SampleOrder>().Build();

        private static AttributeMetadata Attr(EntityMetadata meta, string logicalName) =>
            meta.Attributes.Single(a => a.LogicalName == logicalName);

        [Fact]
        public void Derives_logical_name_primary_id_and_name()
        {
            EntityMetadata meta = Derived();

            Assert.Equal("vela_order", meta.LogicalName);
            Assert.Equal("vela_orderid", meta.PrimaryIdAttribute);
            Assert.Equal("name", meta.PrimaryNameAttribute);
        }

        [Fact]
        public void Infers_user_ownership_from_ownerid_column()
        {
            Assert.Equal(OwnershipTypes.UserOwned, Derived().OwnershipType);
        }

        [Theory]
        [InlineData("vela_orderid", AttributeTypeCode.Uniqueidentifier)]
        [InlineData("name", AttributeTypeCode.String)]
        [InlineData("vela_quantity", AttributeTypeCode.Integer)]
        [InlineData("vela_total", AttributeTypeCode.Money)]
        [InlineData("vela_isrush", AttributeTypeCode.Boolean)]
        [InlineData("vela_due", AttributeTypeCode.DateTime)]
        [InlineData("vela_customerid", AttributeTypeCode.Lookup)]
        [InlineData("statuscode", AttributeTypeCode.Picklist)]
        [InlineData("ownerid", AttributeTypeCode.Lookup)]
        public void Maps_clr_types_to_attribute_type_codes(string logicalName, AttributeTypeCode expected)
        {
            Assert.Equal(expected, Attr(Derived(), logicalName).AttributeType);
        }

        [Fact]
        public void Derives_picklist_options_from_generated_enum()
        {
            PicklistAttributeMetadata status = Assert.IsType<PicklistAttributeMetadata>(Attr(Derived(), "statuscode"));

            (int Value, string Label)[] options = status.OptionSet.Options
                .Select(o => (o.Value!.Value, o.Label.LocalizedLabels.Single().Label))
                .OrderBy(o => o.Item1)
                .ToArray();

            Assert.Equal(new[] { (1, "Draft"), (2, "Active"), (7, "Closed") }, options);
        }

        [Fact]
        public void Recovers_max_length_from_the_StringLength_annotation()
        {
            StringAttributeMetadata name = Assert.IsType<StringAttributeMetadata>(Attr(Derived(), "name"));
            Assert.Equal(160, name.MaxLength);
        }

        [Fact]
        public void Recovers_integer_range_as_int_from_the_Range_annotation()
        {
            IntegerAttributeMetadata quantity = Assert.IsType<IntegerAttributeMetadata>(Attr(Derived(), "vela_quantity"));
            Assert.Equal(1, quantity.MinValue);
            Assert.Equal(1000, quantity.MaxValue);
        }

        [Fact]
        public void Recovers_money_range_as_double_from_the_Range_annotation()
        {
            MoneyAttributeMetadata total = Assert.IsType<MoneyAttributeMetadata>(Attr(Derived(), "vela_total"));
            Assert.Equal(0d, total.MinValue);
            Assert.Equal(1000000d, total.MaxValue);
        }

        [Fact]
        public void Recovers_decimal_range_as_decimal_from_the_Range_annotation()
        {
            DecimalAttributeMetadata weight = Assert.IsType<DecimalAttributeMetadata>(Attr(Derived(), "vela_weight"));
            Assert.Equal(0.5m, weight.MinValue);
            Assert.Equal(99.5m, weight.MaxValue);
        }

        [Fact]
        public void Recovers_double_range_as_double_from_the_Range_annotation()
        {
            DoubleAttributeMetadata ratio = Assert.IsType<DoubleAttributeMetadata>(Attr(Derived(), "vela_ratio"));
            Assert.Equal(-1.5d, ratio.MinValue);
            Assert.Equal(1.5d, ratio.MaxValue);
        }

        [Fact]
        public void Skips_partylist_columns_without_a_faithful_mapping()
        {
            Assert.DoesNotContain(Derived().Attributes, a => a.LogicalName == "vela_recipients");
        }

        [Fact]
        public void Overrides_compose_on_top_of_the_derived_base()
        {
            EntityMetadata meta = FakeEntityMetadata.From<SampleOrder>()
                .On("vela_customerid").Targets("contact")
                .Build();

            LookupAttributeMetadata customer = Assert.IsType<LookupAttributeMetadata>(Attr(meta, "vela_customerid"));
            Assert.Equal(new[] { "contact" }, customer.Targets);
        }

        [Fact]
        public void On_throws_for_an_attribute_that_was_not_derived()
        {
            Assert.Throws<InvalidOperationException>(() => FakeEntityMetadata.From<SampleOrder>().On("vela_recipients"));
        }

        [Fact]
        public void Shorthand_registers_metadata_readable_via_RetrieveEntity()
        {
            var crm = new InMemoryOrganizationService().WithEntityMetadata<SampleOrder>();

            var response = (RetrieveEntityResponse)crm.Execute(
                new RetrieveEntityRequest { LogicalName = "vela_order", EntityFilters = EntityFilters.All });

            Assert.Equal("vela_orderid", response.EntityMetadata.PrimaryIdAttribute);
            Assert.Contains(response.EntityMetadata.Attributes, a => a.LogicalName == "vela_total");
        }
    }
}
