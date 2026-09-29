using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Vela.Cli;

/// <summary>
/// Offline metadata reader for <c>vela models --from-solution</c>: builds <see cref="EntityMetadata"/> from the
/// <c>customizations.xml</c> inside an exported (unmanaged) solution zip, so early-bound models and option-set enums
/// can be generated without a live org connection. The result is fed to the same <see cref="ModelGenerator"/> the
/// online path uses - there is no separate emitter.
/// <para>
/// An exported solution carries full metadata only for the custom tables/columns it contains (system tables are
/// exported as unmodified shells with no attributes). Anything the export does not carry degrades gracefully: an
/// attribute of an unmapped type is skipped, a lookup keeps no typed targets (the export stores numeric type codes,
/// not logical names), a global option set not present in the file falls back to a raw option-set value, and
/// relationships are not emitted (a first version scopes to scalar / lookup / option-set columns).
/// </para>
/// </summary>
internal static class SolutionMetadataReader
{
    /// <summary>Reads table metadata from an exported solution zip (its <c>customizations.xml</c>).</summary>
    /// <param name="zipPath">Path to the unmanaged solution zip.</param>
    /// <param name="entityFilter">Optional logical names to keep; null/empty keeps every table with attributes.</param>
    public static IReadOnlyList<EntityMetadata> ReadFromSolutionZip(string zipPath, IEnumerable<string>? entityFilter = null)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            throw new ArgumentException("A solution zip path is required.", nameof(zipPath));
        }

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "customizations.xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The solution zip does not contain a customizations.xml (is it an exported solution?).");

        using Stream stream = entry.Open();
        using StreamReader reader = new StreamReader(stream);
        return ReadFromCustomizationsXml(reader.ReadToEnd(), entityFilter);
    }

    /// <summary>Reads table metadata from a <c>customizations.xml</c> document (exposed for testing).</summary>
    public static IReadOnlyList<EntityMetadata> ReadFromCustomizationsXml(string customizationsXml, IEnumerable<string>? entityFilter = null)
    {
        if (string.IsNullOrWhiteSpace(customizationsXml))
        {
            throw new ArgumentException("The customizations.xml content is empty.", nameof(customizationsXml));
        }

        XDocument document = XDocument.Parse(customizationsXml);
        XElement root = document.Root ?? throw new InvalidOperationException("customizations.xml has no root element.");

        // Global option sets are declared once at the top level and referenced by name from attributes; parse them
        // first so a referencing attribute reuses the same instance (the generator dedups a global by identity).
        Dictionary<string, OptionSetMetadata> globals = ParseGlobalOptionSets(root);

        HashSet<string>? filter = null;
        if (entityFilter != null)
        {
            filter = new HashSet<string>(entityFilter.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            if (filter.Count == 0)
            {
                filter = null;
            }
        }

        List<EntityMetadata> entities = new List<EntityMetadata>();
        XElement? entitiesElement = Child(root, "Entities");
        if (entitiesElement == null)
        {
            return entities;
        }

        foreach (XElement entityElement in Children(entitiesElement, "Entity"))
        {
            EntityMetadata? metadata = ParseEntity(entityElement, globals, filter);
            if (metadata != null)
            {
                entities.Add(metadata);
            }
        }

        return entities;
    }

    private static EntityMetadata? ParseEntity(XElement entityElement, IReadOnlyDictionary<string, OptionSetMetadata> globals, HashSet<string>? filter)
    {
        // <Name LocalizedName="Account" ...>Account</Name> carries the schema name as text; the logical name is its
        // lowercase form (a Dataverse logical name is always the schema name lowercased).
        XElement? nameElement = Child(entityElement, "Name");
        string schemaName = (nameElement?.Value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(schemaName))
        {
            return null;
        }

        string logicalName = schemaName.ToLowerInvariant();
        if (filter != null && !filter.Contains(logicalName))
        {
            return null;
        }

        XElement? inner = Child(Child(entityElement, "EntityInfo"), "entity");
        XElement? attributesElement = Child(inner, "attributes");
        if (attributesElement == null)
        {
            return null;
        }

        List<AttributeMetadata> attributes = new List<AttributeMetadata>();
        string? primaryId = null;
        foreach (XElement attributeElement in Children(attributesElement, "attribute"))
        {
            AttributeMetadata? attribute = ParseAttribute(attributeElement, globals, out bool isPrimaryId);
            if (attribute == null)
            {
                continue;
            }

            attributes.Add(attribute);
            if (isPrimaryId)
            {
                primaryId = attribute.LogicalName;
            }
        }

        // Nothing to emit for a table with no usable columns (a system-table shell, or a table whose columns are all
        // of unmapped types). Skip it rather than writing an empty class.
        if (attributes.Count == 0)
        {
            return null;
        }

        EntityMetadata entity = new EntityMetadata { SchemaName = schemaName };
        Set(entity, nameof(EntityMetadata.LogicalName), logicalName);
        if (!string.IsNullOrEmpty(primaryId))
        {
            Set(entity, nameof(EntityMetadata.PrimaryIdAttribute), primaryId);
        }

        Label? display = ReadLabel(nameElement, "LocalizedName") ?? ReadLabels(Child(inner, "LocalizedNames"), "LocalizedName");
        if (display != null)
        {
            Set(entity, nameof(EntityMetadata.DisplayName), display);
        }

        Set(entity, nameof(EntityMetadata.Attributes), attributes.ToArray());
        return entity;
    }

    private static AttributeMetadata? ParseAttribute(XElement attributeElement, IReadOnlyDictionary<string, OptionSetMetadata> globals, out bool isPrimaryId)
    {
        isPrimaryId = false;
        string type = (Child(attributeElement, "Type")?.Value ?? string.Empty).Trim().ToLowerInvariant();
        if (type.Length == 0)
        {
            return null;
        }

        string logicalName = (Child(attributeElement, "LogicalName")?.Value ?? string.Empty).Trim().ToLowerInvariant();
        string schemaName = (Attribute(attributeElement, "PhysicalName") ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(logicalName))
        {
            logicalName = schemaName.ToLowerInvariant();
        }

        if (string.IsNullOrEmpty(logicalName))
        {
            return null;
        }

        if (string.IsNullOrEmpty(schemaName))
        {
            schemaName = logicalName;
        }

        if (!TryCreateAttribute(type, out AttributeMetadata attribute, out AttributeTypeCode typeCode, out bool isPicklistLike))
        {
            return null; // Unmapped type (virtual, managedproperty, timezone, ...): degrade by skipping.
        }

        isPrimaryId = type == "primarykey";

        Set(attribute, nameof(AttributeMetadata.LogicalName), logicalName);
        attribute.SchemaName = schemaName;
        Set(attribute, nameof(AttributeMetadata.AttributeType), typeCode);

        // A companion (the "<name>name" pseudo-column of a lookup/option set) carries AttributeOf; the generator
        // skips it, so preserve it faithfully.
        string? attributeOf = Child(attributeElement, "AttributeOf")?.Value?.Trim();
        if (!string.IsNullOrEmpty(attributeOf))
        {
            Set(attribute, nameof(AttributeMetadata.AttributeOf), attributeOf!.ToLowerInvariant());
        }

        ApplyRequiredLevel(attribute, Child(attributeElement, "RequiredLevel")?.Value);
        ApplyRange(attribute, attributeElement);

        Label? display = ReadLabels(Child(attributeElement, "displaynames"), "displayname");
        if (display != null)
        {
            Set(attribute, nameof(AttributeMetadata.DisplayName), display);
        }

        Label? description = ReadLabels(Child(attributeElement, "Descriptions"), "Description");
        if (description != null)
        {
            Set(attribute, nameof(AttributeMetadata.Description), description);
        }

        if (isPicklistLike)
        {
            ApplyOptionSet(attribute, attributeElement, globals);
        }

        return attribute;
    }

    // Maps a customizations.xml <Type> to an SDK attribute-metadata instance + type code. Returns false for a type
    // the model generator does not map (so the caller skips it). isPicklistLike marks the enum types that carry an
    // option set.
    private static bool TryCreateAttribute(string type, out AttributeMetadata attribute, out AttributeTypeCode typeCode, out bool isPicklistLike)
    {
        isPicklistLike = false;
        switch (type)
        {
            case "nvarchar":
            case "varchar":
            case "char":
            case "nchar":
                attribute = new StringAttributeMetadata();
                typeCode = AttributeTypeCode.String;
                return true;
            case "ntext":
            case "text":
                attribute = new MemoAttributeMetadata();
                typeCode = AttributeTypeCode.Memo;
                return true;
            case "int":
            case "smallint":
            case "tinyint":
                attribute = new IntegerAttributeMetadata();
                typeCode = AttributeTypeCode.Integer;
                return true;
            case "bigint":
                attribute = new BigIntAttributeMetadata();
                typeCode = AttributeTypeCode.BigInt;
                return true;
            case "decimal":
            case "numeric":
                attribute = new DecimalAttributeMetadata();
                typeCode = AttributeTypeCode.Decimal;
                return true;
            case "float":
            case "real":
                attribute = new DoubleAttributeMetadata();
                typeCode = AttributeTypeCode.Double;
                return true;
            case "money":
            case "smallmoney":
                attribute = new MoneyAttributeMetadata();
                typeCode = AttributeTypeCode.Money;
                return true;
            case "bit":
                attribute = new BooleanAttributeMetadata();
                typeCode = AttributeTypeCode.Boolean;
                return true;
            case "datetime":
            case "smalldatetime":
                attribute = new DateTimeAttributeMetadata();
                typeCode = AttributeTypeCode.DateTime;
                return true;
            case "uniqueidentifier":
            case "primarykey":
                attribute = new UniqueIdentifierAttributeMetadata();
                typeCode = AttributeTypeCode.Uniqueidentifier;
                return true;
            case "lookup":
                attribute = new LookupAttributeMetadata();
                typeCode = AttributeTypeCode.Lookup;
                return true;
            case "customer":
                attribute = new LookupAttributeMetadata();
                typeCode = AttributeTypeCode.Customer;
                return true;
            case "owner":
                attribute = new LookupAttributeMetadata();
                typeCode = AttributeTypeCode.Owner;
                return true;
            case "partylist":
                attribute = new LookupAttributeMetadata();
                typeCode = AttributeTypeCode.PartyList;
                return true;
            case "picklist":
                attribute = new PicklistAttributeMetadata();
                typeCode = AttributeTypeCode.Picklist;
                isPicklistLike = true;
                return true;
            case "state":
                attribute = new StateAttributeMetadata();
                typeCode = AttributeTypeCode.State;
                isPicklistLike = true;
                return true;
            case "status":
                attribute = new StatusAttributeMetadata();
                typeCode = AttributeTypeCode.Status;
                isPicklistLike = true;
                return true;
            case "multiselectpicklist":
                attribute = new MultiSelectPicklistAttributeMetadata();
                typeCode = AttributeTypeCode.Virtual;
                isPicklistLike = true;
                return true;
            default:
                attribute = null!;
                typeCode = default;
                return false;
        }
    }

    private static void ApplyRequiredLevel(AttributeMetadata attribute, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (Enum.TryParse(value!.Trim(), ignoreCase: true, out AttributeRequiredLevel level))
        {
            Set(attribute, nameof(AttributeMetadata.RequiredLevel), new AttributeRequiredLevelManagedProperty(level));
        }
    }

    private static void ApplyRange(AttributeMetadata attribute, XElement attributeElement)
    {
        string? min = Child(attributeElement, "MinValue")?.Value;
        string? max = Child(attributeElement, "MaxValue")?.Value;

        switch (attribute)
        {
            case StringAttributeMetadata:
            case MemoAttributeMetadata:
                if (TryInt(Child(attributeElement, "MaxLength")?.Value ?? Child(attributeElement, "Length")?.Value, out int maxLength))
                {
                    Set(attribute, "MaxLength", maxLength);
                }

                break;
            case IntegerAttributeMetadata:
                if (TryInt(min, out int imin))
                {
                    Set(attribute, "MinValue", imin);
                }

                if (TryInt(max, out int imax))
                {
                    Set(attribute, "MaxValue", imax);
                }

                break;
            case BigIntAttributeMetadata:
                if (TryLong(min, out long lmin))
                {
                    Set(attribute, "MinValue", lmin);
                }

                if (TryLong(max, out long lmax))
                {
                    Set(attribute, "MaxValue", lmax);
                }

                break;
            case DecimalAttributeMetadata:
                if (TryDecimal(min, out decimal dmin))
                {
                    Set(attribute, "MinValue", dmin);
                }

                if (TryDecimal(max, out decimal dmax))
                {
                    Set(attribute, "MaxValue", dmax);
                }

                break;
            case DoubleAttributeMetadata:
            case MoneyAttributeMetadata:
                if (TryDouble(min, out double omin))
                {
                    Set(attribute, "MinValue", omin);
                }

                if (TryDouble(max, out double omax))
                {
                    Set(attribute, "MaxValue", omax);
                }

                break;
        }
    }

    private static void ApplyOptionSet(AttributeMetadata attribute, XElement attributeElement, IReadOnlyDictionary<string, OptionSetMetadata> globals)
    {
        // A global reference: reuse the shared instance so the generator emits one shared enum.
        string? globalName = Child(attributeElement, "OptionSetName")?.Value?.Trim();
        if (!string.IsNullOrEmpty(globalName) && globals.TryGetValue(globalName!, out OptionSetMetadata? global))
        {
            AssignOptionSet(attribute, global);
            return;
        }

        // A local (inline) option set.
        XElement? inline = Child(attributeElement, "optionset");
        if (inline != null)
        {
            OptionSetMetadata local = ParseOptionSet(inline, isGlobal: false);
            if (local.Options.Count > 0)
            {
                AssignOptionSet(attribute, local);
            }
        }
    }

    private static void AssignOptionSet(AttributeMetadata attribute, OptionSetMetadata optionSet)
    {
        // Picklist / State / Status and MultiSelectPicklist all derive from EnumAttributeMetadata, which owns the
        // OptionSet property.
        if (attribute is EnumAttributeMetadata enumAttribute)
        {
            enumAttribute.OptionSet = optionSet;
        }
    }

    private static Dictionary<string, OptionSetMetadata> ParseGlobalOptionSets(XElement root)
    {
        Dictionary<string, OptionSetMetadata> globals = new Dictionary<string, OptionSetMetadata>(StringComparer.OrdinalIgnoreCase);
        XElement? optionSets = Child(root, "optionsets");
        if (optionSets == null)
        {
            return globals;
        }

        foreach (XElement optionSet in Children(optionSets, "optionset"))
        {
            string? name = Attribute(optionSet, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            globals[name!] = ParseOptionSet(optionSet, isGlobal: true);
        }

        return globals;
    }

    private static OptionSetMetadata ParseOptionSet(XElement optionSetElement, bool isGlobal)
    {
        OptionSetMetadata optionSet = new OptionSetMetadata { IsGlobal = isGlobal };
        if (isGlobal)
        {
            optionSet.Name = Attribute(optionSetElement, "Name");
        }

        Label? display = ReadLabels(Child(optionSetElement, "displaynames"), "displayname");
        if (display != null)
        {
            optionSet.DisplayName = display;
        }

        // Options live under <options>/<option> (picklist/multiselect) or <states>/<state> and <statuses>/<status>
        // (state/status). Read whichever container is present.
        foreach (XElement container in optionSetElement.Elements()
            .Where(e => Is(e, "options") || Is(e, "states") || Is(e, "statuses")))
        {
            foreach (XElement option in container.Elements())
            {
                if (!TryInt(Attribute(option, "value"), out int value))
                {
                    continue;
                }

                Label label = ReadLabels(Child(option, "labels"), "label") ?? new Label();
                optionSet.Options.Add(new OptionMetadata(label, value));
            }
        }

        return optionSet;
    }

    // ---- XML helpers (case-insensitive on element/attribute local names, to tolerate export variations) --------

    private static bool Is(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => Is(e, name));

    private static IEnumerable<XElement> Children(XElement? parent, string name) =>
        parent?.Elements().Where(e => Is(e, name)) ?? Enumerable.Empty<XElement>();

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    // A single label from an element's attribute (e.g. Name@LocalizedName).
    private static Label? ReadLabel(XElement? element, string attributeName)
    {
        if (element == null)
        {
            return null;
        }

        string? text = Attribute(element, attributeName);
        return string.IsNullOrEmpty(text) ? null : new Label(text, 1033);
    }

    // A label from a localized-labels container (<displaynames>/<displayname>, <labels>/<label>, ...), preferring
    // language 1033, else the first entry.
    private static Label? ReadLabels(XElement? container, string itemName)
    {
        if (container == null)
        {
            return null;
        }

        List<XElement> items = Children(container, itemName).ToList();
        if (items.Count == 0)
        {
            return null;
        }

        XElement chosen = items.FirstOrDefault(e => Attribute(e, "languagecode") == "1033") ?? items[0];
        string? text = Attribute(chosen, "description");
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        int lcid = TryInt(Attribute(chosen, "languagecode"), out int parsed) ? parsed : 1033;
        return new Label(text, lcid);
    }

    private static bool TryInt(string? value, out int result) =>
        int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryLong(string? value, out long result) =>
        long.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryDecimal(string? value, out decimal result) =>
        decimal.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static bool TryDouble(string? value, out double result) =>
        double.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    // Sets an SDK metadata property through its non-public setter (the metadata types are read-only over the wire),
    // matching how the test metadata factory hydrates them.
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
