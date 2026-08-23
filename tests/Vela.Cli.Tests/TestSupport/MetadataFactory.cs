using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Vela.Cli.Tests.TestSupport;

/// <summary>
/// Builds <see cref="EntityMetadata"/> for <see cref="ModelGenerator"/> tests. Lives here (net10) because the
/// net462 <c>FakeEntityMetadata</c> can't be referenced from the CLI test project. Sets the SDK's read-only
/// metadata properties through their non-public setters, like the harness does.
/// </summary>
internal sealed class MetadataFactory
{
    private readonly EntityMetadata _entity = new EntityMetadata();
    private readonly List<AttributeMetadata> _attributes = new List<AttributeMetadata>();
    private readonly List<EntityKeyMetadata> _keys = new List<EntityKeyMetadata>();
    private AttributeMetadata? _last;

    private MetadataFactory(string logicalName, string schemaName)
    {
        Set(_entity, nameof(EntityMetadata.LogicalName), logicalName);
        _entity.SchemaName = schemaName;
    }

    public static MetadataFactory Entity(string logicalName, string schemaName) => new MetadataFactory(logicalName, schemaName);

    public MetadataFactory PrimaryId(string logicalName, string schemaName)
    {
        Set(_entity, nameof(EntityMetadata.PrimaryIdAttribute), logicalName);
        return Add(new UniqueIdentifierAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Uniqueidentifier);
    }

    public MetadataFactory String(string logicalName, string schemaName, int? maxLength = null)
    {
        Add(new StringAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.String);
        if (maxLength.HasValue)
        {
            Set(_last!, "MaxLength", maxLength.Value);
        }

        return this;
    }

    public MetadataFactory Memo(string logicalName, string schemaName, int? maxLength = null, StringFormat? format = null)
    {
        Add(new MemoAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Memo);
        if (maxLength.HasValue)
        {
            Set(_last!, "MaxLength", maxLength.Value);
        }

        if (format.HasValue)
        {
            Set(_last!, "Format", format.Value);
        }

        return this;
    }

    public MetadataFactory StringFormat(StringFormat format)
    {
        Set(_last!, "Format", format);
        return this;
    }

    public MetadataFactory Integer(string logicalName, string schemaName, int? min = null, int? max = null)
    {
        Add(new IntegerAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Integer);
        if (min.HasValue)
        {
            Set(_last!, "MinValue", min.Value);
        }

        if (max.HasValue)
        {
            Set(_last!, "MaxValue", max.Value);
        }

        return this;
    }

    public MetadataFactory BigInt(string logicalName, string schemaName, long? min = null, long? max = null)
    {
        Add(new BigIntAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.BigInt);
        if (min.HasValue)
        {
            Set(_last!, "MinValue", min.Value);
        }

        if (max.HasValue)
        {
            Set(_last!, "MaxValue", max.Value);
        }

        return this;
    }

    public MetadataFactory Decimal(string logicalName, string schemaName, decimal? min = null, decimal? max = null, int? precision = null)
    {
        Add(new DecimalAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Decimal);
        if (min.HasValue)
        {
            Set(_last!, "MinValue", min.Value);
        }

        if (max.HasValue)
        {
            Set(_last!, "MaxValue", max.Value);
        }

        if (precision.HasValue)
        {
            Set(_last!, "Precision", precision.Value);
        }

        return this;
    }

    public MetadataFactory Double(string logicalName, string schemaName, double? min = null, double? max = null, int? precision = null)
    {
        Add(new DoubleAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Double);
        if (min.HasValue)
        {
            Set(_last!, "MinValue", min.Value);
        }

        if (max.HasValue)
        {
            Set(_last!, "MaxValue", max.Value);
        }

        if (precision.HasValue)
        {
            Set(_last!, "Precision", precision.Value);
        }

        return this;
    }

    public MetadataFactory Boolean(string logicalName, string schemaName)
        => Add(new BooleanAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Boolean);

    public MetadataFactory DateTime(string logicalName, string schemaName, DateTimeFormat? format = null)
    {
        Add(new DateTimeAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.DateTime);
        if (format.HasValue)
        {
            Set(_last!, "Format", format.Value);
        }

        return this;
    }

    public MetadataFactory Money(string logicalName, string schemaName, decimal? min = null, decimal? max = null, int? precision = null)
    {
        Add(new MoneyAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Money);
        if (min.HasValue)
        {
            Set(_last!, "MinValue", (double)min.Value);
        }

        if (max.HasValue)
        {
            Set(_last!, "MaxValue", (double)max.Value);
        }

        if (precision.HasValue)
        {
            Set(_last!, "Precision", precision.Value);
        }

        return this;
    }

    public MetadataFactory PartyList(string logicalName, string schemaName, params string[] targets)
    {
        Add(new LookupAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.PartyList);
        if (targets.Length > 0)
        {
            Set(_last!, "Targets", targets);
        }

        return this;
    }

    public MetadataFactory Lookup(string logicalName, string schemaName, params string[] targets)
    {
        Add(new LookupAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Lookup);
        Set(_last!, "Targets", targets);
        return this;
    }

    // File/Image columns have no dedicated AttributeTypeCode; the generator detects them by metadata subclass.
    public MetadataFactory File(string logicalName, string schemaName)
        => Add(new FileAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Virtual);

    public MetadataFactory Image(string logicalName, string schemaName)
        => Add(new ImageAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Virtual);

    public MetadataFactory Customer(string logicalName, string schemaName, params string[] targets)
    {
        Add(new LookupAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Customer);
        Set(_last!, "Targets", targets);
        return this;
    }

    public MetadataFactory Owner(string logicalName, string schemaName, params string[] targets)
    {
        Add(new LookupAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Owner);
        if (targets.Length > 0)
        {
            Set(_last!, "Targets", targets);
        }

        return this;
    }

    public MetadataFactory Picklist(string logicalName, string schemaName)
        => Add(new PicklistAttributeMetadata(), logicalName, schemaName, AttributeTypeCode.Picklist);

    public MetadataFactory Choice(string logicalName, string schemaName, bool global, string? globalName, params (int value, string label)[] options)
    {
        PicklistAttributeMetadata attribute = new PicklistAttributeMetadata();
        Add(attribute, logicalName, schemaName, AttributeTypeCode.Picklist);
        OptionSetMetadata optionSet = new OptionSetMetadata { IsGlobal = global };
        if (global)
        {
            optionSet.Name = globalName;
        }

        foreach ((int value, string label) in options)
        {
            optionSet.Options.Add(new OptionMetadata(new Label(label, 1033), value));
        }

        attribute.OptionSet = optionSet;
        return this;
    }

    public MetadataFactory MultiSelect(string logicalName, string schemaName, bool global, string? globalName, params (int value, string label)[] options)
    {
        MultiSelectPicklistAttributeMetadata attribute = new MultiSelectPicklistAttributeMetadata();
        Add(attribute, logicalName, schemaName, AttributeTypeCode.Virtual);
        OptionSetMetadata optionSet = new OptionSetMetadata { IsGlobal = global };
        if (global)
        {
            optionSet.Name = globalName;
        }

        foreach ((int value, string label) in options)
        {
            optionSet.Options.Add(new OptionMetadata(new Label(label, 1033), value));
        }

        attribute.OptionSet = optionSet;
        return this;
    }

    public MetadataFactory Display(string label)
    {
        Set(_last!, nameof(AttributeMetadata.DisplayName), new Label(label, 1033));
        return this;
    }

    public MetadataFactory Describe(string label)
    {
        Set(_last!, nameof(AttributeMetadata.Description), new Label(label, 1033));
        return this;
    }

    public MetadataFactory Required(AttributeRequiredLevel level)
    {
        Set(_last!, nameof(AttributeMetadata.RequiredLevel), new AttributeRequiredLevelManagedProperty(level));
        return this;
    }

    // Marks the last attribute as a virtual companion of another (e.g. the "<name>name" of a picklist/lookup),
    // which the generator skips.
    public MetadataFactory Companion(string attributeOf)
    {
        Set(_last!, nameof(AttributeMetadata.AttributeOf), attributeOf);
        return this;
    }

    public MetadataFactory Key(string logicalName, string schemaName, params string[] keyAttributes)
    {
        EntityKeyMetadata key = new EntityKeyMetadata();
        Set(key, nameof(EntityKeyMetadata.LogicalName), logicalName);
        Set(key, nameof(EntityKeyMetadata.SchemaName), schemaName);
        Set(key, nameof(EntityKeyMetadata.KeyAttributes), keyAttributes);
        _keys.Add(key);
        return this;
    }

    public EntityMetadata Build()
    {
        Set(_entity, nameof(EntityMetadata.Attributes), _attributes.ToArray());
        if (_keys.Count > 0)
        {
            Set(_entity, nameof(EntityMetadata.Keys), _keys.ToArray());
        }

        return _entity;
    }

    private MetadataFactory Add(AttributeMetadata attribute, string logicalName, string schemaName, AttributeTypeCode type)
    {
        Set(attribute, nameof(AttributeMetadata.LogicalName), logicalName);
        attribute.SchemaName = schemaName;
        Set(attribute, nameof(AttributeMetadata.AttributeType), type);
        _attributes.Add(attribute);
        _last = attribute;
        return this;
    }

    private static void Set(object target, string property, object? value)
    {
        PropertyInfo? info = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo? setter = info?.GetSetMethod(nonPublic: true);
        if (setter == null)
        {
            throw new InvalidOperationException($"Cannot set metadata property '{property}' on {target.GetType().Name}.");
        }

        setter.Invoke(target, new[] { value });
    }
}
