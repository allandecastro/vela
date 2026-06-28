using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Vela.Testing
{
    /// <summary>
    /// Builds <see cref="EntityMetadata"/> for the in-memory metadata service without fighting the SDK's
    /// read-only metadata setters. Register the result with <c>WithEntityMetadata(...)</c>, then a plugin's
    /// <c>RetrieveEntityRequest</c>/<c>RetrieveAttributeRequest</c> reads it back. Only the fields tests
    /// usually need are exposed; everything else stays at its SDK default.
    /// </summary>
    public sealed class FakeEntityMetadata
    {
        private readonly EntityMetadata _entity = new EntityMetadata();
        private readonly List<AttributeMetadata> _attributes = new List<AttributeMetadata>();
        private readonly List<ManyToManyRelationshipMetadata> _manyToMany = new List<ManyToManyRelationshipMetadata>();

        private FakeEntityMetadata(string logicalName)
        {
            SetSealed(_entity, nameof(EntityMetadata.LogicalName), logicalName);
            _entity.SchemaName = logicalName;
        }

        /// <summary>Starts a metadata definition for the given table logical name.</summary>
        public static FakeEntityMetadata For(string logicalName) => new FakeEntityMetadata(logicalName);

        /// <summary>Sets the primary id attribute (and adds it as a Uniqueidentifier column).</summary>
        public FakeEntityMetadata PrimaryId(string attributeLogicalName)
        {
            SetSealed(_entity, nameof(EntityMetadata.PrimaryIdAttribute), attributeLogicalName);
            return Attribute(attributeLogicalName, AttributeTypeCode.Uniqueidentifier);
        }

        /// <summary>Sets the primary name attribute (and adds it as a String column).</summary>
        public FakeEntityMetadata PrimaryName(string attributeLogicalName)
        {
            SetSealed(_entity, nameof(EntityMetadata.PrimaryNameAttribute), attributeLogicalName);
            return Attribute(attributeLogicalName, AttributeTypeCode.String);
        }

        /// <summary>Sets the ownership type (user/team-owned, organization-owned, …).</summary>
        public FakeEntityMetadata Ownership(OwnershipTypes ownership)
        {
            _entity.OwnershipType = ownership;
            return this;
        }

        /// <summary>Overrides the entity schema name (used as the generated class name; defaults to the logical name).</summary>
        public FakeEntityMetadata Schema(string schemaName)
        {
            _entity.SchemaName = schemaName;
            return this;
        }

        /// <summary>Adds an attribute of the given type, optionally with a distinct schema name (PascalCase identifier).</summary>
        public FakeEntityMetadata Attribute(string logicalName, AttributeTypeCode type, string? schemaName = null)
        {
            AttributeMetadata attribute = CreateAttribute(type);
            SetSealed(attribute, nameof(AttributeMetadata.LogicalName), logicalName);
            attribute.SchemaName = schemaName ?? logicalName;
            SetSealed(attribute, nameof(AttributeMetadata.AttributeType), type);
            _attributes.Add(attribute);
            return this;
        }

        /// <summary>Sets the display-name label on the most recently added attribute.</summary>
        public FakeEntityMetadata DisplayName(string label)
        {
            SetSealed(Last, nameof(AttributeMetadata.DisplayName), new Label(label, 1033));
            return this;
        }

        /// <summary>Sets the description label on the most recently added attribute.</summary>
        public FakeEntityMetadata Description(string label)
        {
            SetSealed(Last, nameof(AttributeMetadata.Description), new Label(label, 1033));
            return this;
        }

        /// <summary>Sets the required level on the most recently added attribute.</summary>
        public FakeEntityMetadata Required(AttributeRequiredLevel level)
        {
            SetSealed(Last, nameof(AttributeMetadata.RequiredLevel), new AttributeRequiredLevelManagedProperty(level));
            return this;
        }

        /// <summary>Sets the maximum length on the most recently added String/Memo attribute.</summary>
        public FakeEntityMetadata MaxLength(int maxLength)
        {
            SetSealed(Last, "MaxLength", maxLength);
            return this;
        }

        /// <summary>Sets the numeric precision on the most recently added Decimal/Double/Money attribute.</summary>
        public FakeEntityMetadata Precision(int precision)
        {
            SetSealed(Last, "Precision", precision);
            return this;
        }

        /// <summary>Sets the min/max on the most recently added Integer attribute.</summary>
        public FakeEntityMetadata Range(int min, int max)
        {
            SetSealed(Last, "MinValue", min);
            SetSealed(Last, "MaxValue", max);
            return this;
        }

        /// <summary>Sets the min/max on the most recently added Decimal/Money attribute.</summary>
        public FakeEntityMetadata Range(decimal min, decimal max)
        {
            SetSealed(Last, "MinValue", min);
            SetSealed(Last, "MaxValue", max);
            return this;
        }

        /// <summary>Sets the lookup target tables on the most recently added Lookup attribute.</summary>
        public FakeEntityMetadata Targets(params string[] targets)
        {
            SetSealed(Last, "Targets", targets);
            return this;
        }

        private AttributeMetadata Last =>
            _attributes.Count > 0
                ? _attributes[_attributes.Count - 1]
                : throw new InvalidOperationException("Add an attribute before configuring it.");

        /// <summary>Adds a Picklist (choice) attribute with the given options.</summary>
        public FakeEntityMetadata Picklist(string logicalName, params (int value, string label)[] options)
        {
            PicklistAttributeMetadata attribute = new PicklistAttributeMetadata { SchemaName = logicalName };
            SetSealed(attribute, nameof(AttributeMetadata.LogicalName), logicalName);
            SetSealed(attribute, nameof(AttributeMetadata.AttributeType), AttributeTypeCode.Picklist);

            OptionSetMetadata optionSet = new OptionSetMetadata();
            foreach ((int value, string label) in options)
            {
                optionSet.Options.Add(new OptionMetadata(new Label(label, 1033), value));
            }

            attribute.OptionSet = optionSet;
            _attributes.Add(attribute);
            return this;
        }

        /// <summary>
        /// Declares a many-to-many relationship carried by this table's metadata. Once registered via
        /// <c>WithEntityMetadata(...)</c>, Associate/Disassociate can resolve the intersect from it, so an
        /// explicit <c>WithManyToMany(...)</c> becomes optional.
        /// </summary>
        public FakeEntityMetadata ManyToMany(string schemaName, string intersectEntity, string entity1, string entity1IntersectAttribute, string entity2, string entity2IntersectAttribute)
        {
            _manyToMany.Add(new ManyToManyRelationshipMetadata
            {
                SchemaName = schemaName,
                IntersectEntityName = intersectEntity,
                Entity1LogicalName = entity1,
                Entity1IntersectAttribute = entity1IntersectAttribute,
                Entity2LogicalName = entity2,
                Entity2IntersectAttribute = entity2IntersectAttribute,
            });
            return this;
        }

        /// <summary>Produces the <see cref="EntityMetadata"/>.</summary>
        public EntityMetadata Build()
        {
            SetSealed(_entity, nameof(EntityMetadata.Attributes), _attributes.ToArray());
            if (_manyToMany.Count > 0)
            {
                SetSealed(_entity, nameof(EntityMetadata.ManyToManyRelationships), _manyToMany.ToArray());
            }

            return _entity;
        }

        private static AttributeMetadata CreateAttribute(AttributeTypeCode type)
        {
            switch (type)
            {
                case AttributeTypeCode.Picklist:
                case AttributeTypeCode.State:
                case AttributeTypeCode.Status:
                    return new PicklistAttributeMetadata();
                case AttributeTypeCode.Integer:
                    return new IntegerAttributeMetadata();
                case AttributeTypeCode.BigInt:
                    return new BigIntAttributeMetadata();
                case AttributeTypeCode.Boolean:
                    return new BooleanAttributeMetadata();
                case AttributeTypeCode.DateTime:
                    return new DateTimeAttributeMetadata();
                case AttributeTypeCode.Decimal:
                    return new DecimalAttributeMetadata();
                case AttributeTypeCode.Double:
                    return new DoubleAttributeMetadata();
                case AttributeTypeCode.Money:
                    return new MoneyAttributeMetadata();
                case AttributeTypeCode.Lookup:
                case AttributeTypeCode.Customer:
                case AttributeTypeCode.Owner:
                    return new LookupAttributeMetadata();
                case AttributeTypeCode.Uniqueidentifier:
                    return new UniqueIdentifierAttributeMetadata();
                case AttributeTypeCode.Memo:
                    return new MemoAttributeMetadata();
                default:
                    return new StringAttributeMetadata();
            }
        }

        // Metadata properties like LogicalName/PrimaryIdAttribute/AttributeType/Attributes are read-only on the
        // SDK types (the platform sets them). Set them through the non-public setter for test fixtures.
        private static void SetSealed(object target, string propertyName, object? value)
        {
            PropertyInfo? property = target.GetType().GetProperty(
                propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo? setter = property?.GetSetMethod(nonPublic: true);
            if (setter == null)
            {
                throw new InvalidOperationException($"Cannot set metadata property '{propertyName}' on {target.GetType().Name}.");
            }

            setter.Invoke(target, new[] { value });
        }
    }
}
