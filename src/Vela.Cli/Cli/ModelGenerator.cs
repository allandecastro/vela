using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk.Metadata;

namespace Vela.Cli;

/// <summary>Options controlling early-bound model generation.</summary>
public sealed record ModelGenOptions(string Namespace, bool DataAnnotations = true);

/// <summary>A generated early-bound model file: its file name and C# source.</summary>
public sealed record GeneratedModel(string FileName, string Code);

/// <summary>
/// Pure early-bound code generator: turns <see cref="EntityMetadata"/> into <c>partial</c> classes following
/// Vela's model conventions (<c>[EntityLogicalName]</c>, <c>EntityLogicalNameValue</c>, typed properties via
/// <c>Get/SetAttributeValue</c>). No Dataverse I/O — feed it metadata (real or <c>FakeEntityMetadata</c>) and it
/// returns the files to write. Deterministic: entities and attributes are emitted in logical-name order.
/// </summary>
public static class ModelGenerator
{
    public static IReadOnlyList<GeneratedModel> Generate(IEnumerable<EntityMetadata> entities, ModelGenOptions options)
    {
        if (entities == null)
        {
            throw new ArgumentNullException(nameof(entities));
        }

        if (options == null || string.IsNullOrWhiteSpace(options.Namespace))
        {
            throw new ArgumentException("A target namespace is required.", nameof(options));
        }

        Dictionary<string, OptionSetMetadata> globals = new Dictionary<string, OptionSetMetadata>(StringComparer.Ordinal);
        HashSet<string> features = new HashSet<string>(StringComparer.Ordinal);

        EntityMetadata[] ordered = entities
            .Where(e => !string.IsNullOrWhiteSpace(e?.LogicalName))
            .OrderBy(e => e.LogicalName, StringComparer.Ordinal)
            .ToArray();

        // Map every generated table's logical name to its class name, so a relationship nav property can be typed
        // to the target class. A relationship whose target table is not in this batch is skipped (its class would
        // not exist), keeping the generated code compilable.
        Dictionary<string, string> logicalToClass = ordered.ToDictionary(e => e.LogicalName, ClassName, StringComparer.Ordinal);

        List<GeneratedModel> files = ordered
            .Select(e => new GeneratedModel($"{ClassName(e)}.cs", GenerateOne(e, options, globals, features, logicalToClass)))
            .ToList();

        // Global option sets are shared across tables → one standalone enum file each.
        foreach (KeyValuePair<string, OptionSetMetadata> global in globals.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            files.Add(new GeneratedModel($"{global.Key.TrimStart('@')}.cs", GenerateGlobalEnumFile(global.Key, global.Value, options.Namespace)));
        }

        // Self-contained support attributes, emitted only when a property actually uses them, so the generated
        // models stay dependency-light (SDK + generated code) yet carry facets no standard annotation can express.
        if (features.Contains(RequiredLevelFeature))
        {
            files.Add(new GeneratedModel("RequiredLevelAttribute.cs", GenerateRequiredLevelAttributeFile(options.Namespace)));
        }

        if (features.Contains(LookupTargetsFeature))
        {
            files.Add(new GeneratedModel("LookupTargetsAttribute.cs", GenerateLookupTargetsAttributeFile(options.Namespace)));
        }

        if (features.Contains(RelationshipSchemaNameFeature))
        {
            files.Add(new GeneratedModel("RelationshipSchemaNameAttribute.cs", GenerateRelationshipSchemaNameAttributeFile(options.Namespace)));
        }

        return files;
    }

    private const string RequiredLevelFeature = "requiredLevel";
    private const string LookupTargetsFeature = "lookupTargets";
    private const string RelationshipSchemaNameFeature = "relationshipSchemaName";

    private static string GenerateOne(EntityMetadata entity, ModelGenOptions options, IDictionary<string, OptionSetMetadata> globals, ISet<string> features, IReadOnlyDictionary<string, string> logicalToClass)
    {
        string className = ClassName(entity);
        string logicalName = entity.LogicalName;
        string primaryId = entity.PrimaryIdAttribute ?? string.Empty;

        AttributeMetadata[] attributes = (entity.Attributes ?? Array.Empty<AttributeMetadata>())
            .Where(a => !string.IsNullOrWhiteSpace(a?.LogicalName) && a.AttributeType.HasValue)
            // Skip virtual companion attributes (those with AttributeOf set) — the "<name>name" formatted-name
            // pseudo-columns of option sets and lookups. They are not storable columns (the formatted text lives on
            // FormattedValues), so emitting them as `object` properties is noise. Multi-select picklists are Virtual
            // too but have no AttributeOf, so they are kept.
            .Where(a => string.IsNullOrEmpty(a.AttributeOf))
            .OrderBy(a => a.LogicalName, StringComparer.Ordinal)
            .ToArray();

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        // The model carries nullable reference annotations (e.g. a typed N:1 nav property `Account? Parent`), so the
        // file opts into a nullable context; without it the consuming project warns CS8669. `enable` (not `annotations`)
        // is safe here because every property is a get/set body over Get/SetAttributeValue, so nothing triggers a
        // nullable flow warning (CS8603/CS8618).
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"namespace {options.Namespace}");
        sb.AppendLine("{");
        sb.AppendLine("    using System;");
        if (options.DataAnnotations)
        {
            sb.AppendLine("    using System.ComponentModel.DataAnnotations;");
        }

        sb.AppendLine("    using Microsoft.Xrm.Sdk;");
        sb.AppendLine("    using Microsoft.Xrm.Sdk.Client;");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>{XmlEscape(EntityDoc(entity))}</summary>");
        sb.AppendLine("    [EntityLogicalName(EntityLogicalNameValue)]");
        sb.AppendLine($"    public partial class {className} : Entity");
        sb.AppendLine("    {");
        sb.AppendLine($"        public const string EntityLogicalNameValue = \"{logicalName}\";");
        sb.AppendLine();
        sb.AppendLine($"        public {className}()");
        sb.AppendLine("            : base(EntityLogicalNameValue)");
        sb.AppendLine("        {");
        sb.AppendLine("        }");

        // Keep Entity.Id in sync with the primary-id attribute both ways.
        if (!string.IsNullOrWhiteSpace(primaryId))
        {
            sb.AppendLine();
            sb.AppendLine("        public override Guid Id");
            sb.AppendLine("        {");
            sb.AppendLine("            get => base.Id;");
            sb.AppendLine($"            set {{ base.Id = value; SetAttributeValue(\"{primaryId}\", value); }}");
            sb.AppendLine("        }");
        }

        HashSet<string> usedNames = new HashSet<string>(StringComparer.Ordinal) { className };
        List<(string Name, OptionSetMetadata OptionSet)> localEnums = new List<(string, OptionSetMetadata)>();
        foreach (AttributeMetadata attribute in attributes)
        {
            AppendProperty(sb, attribute, primaryId, options, usedNames, localEnums, globals, features);
        }

        AppendAlternateKeys(sb, entity, usedNames);

        AppendRelationships(sb, entity, attributes, usedNames, features, logicalToClass);

        // Local (entity-specific) option sets are emitted as enums nested in the table class.
        foreach ((string Name, OptionSetMetadata OptionSet) local in localEnums)
        {
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Choice values for <c>{local.Name}</c>.</summary>");
            sb.AppendLine($"        public enum {local.Name}");
            sb.AppendLine("        {");
            sb.Append(BuildEnumMembers(local.OptionSet, "            "));
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendProperty(StringBuilder sb, AttributeMetadata attribute, string primaryId, ModelGenOptions options, HashSet<string> usedNames, List<(string, OptionSetMetadata)> localEnums, IDictionary<string, OptionSetMetadata> globals, ISet<string> features)
    {
        string logical = attribute.LogicalName;
        string name = UniqueName(PropertyName(attribute), usedNames);
        bool isPrimaryId = string.Equals(logical, primaryId, StringComparison.OrdinalIgnoreCase);

        // Multi-select choice → a list of enum values backed by an OptionSetValueCollection (not a [Flags] enum:
        // Dataverse stores a collection of option values, not a bitmask).
        if (attribute is MultiSelectPicklistAttributeMetadata multi && multi.OptionSet?.Options is { Count: > 0 })
        {
            string msEnum = RegisterEnum(multi.OptionSet, name, usedNames, localEnums, globals);
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>{XmlEscape(AttributeDoc(attribute))}</summary>");
            sb.AppendLine($"        [AttributeLogicalName(\"{logical}\")]");
            AppendFacetAttributes(sb, attribute, features);
            sb.AppendLine($"        public System.Collections.Generic.IList<{msEnum}> {name}");
            sb.AppendLine("        {");
            sb.AppendLine("            get");
            sb.AppendLine("            {");
            sb.AppendLine($"                OptionSetValueCollection raw = GetAttributeValue<OptionSetValueCollection>(\"{logical}\");");
            sb.AppendLine("                if (raw == null) { return null; }");
            sb.AppendLine($"                System.Collections.Generic.List<{msEnum}> values = new System.Collections.Generic.List<{msEnum}>();");
            sb.AppendLine($"                foreach (OptionSetValue option in raw) {{ values.Add(({msEnum})option.Value); }}");
            sb.AppendLine("                return values;");
            sb.AppendLine("            }");
            sb.AppendLine("            set");
            sb.AppendLine("            {");
            sb.AppendLine($"                if (value == null) {{ SetAttributeValue(\"{logical}\", null); return; }}");
            sb.AppendLine("                OptionSetValueCollection raw = new OptionSetValueCollection();");
            sb.AppendLine($"                foreach ({msEnum} option in value) {{ raw.Add(new OptionSetValue((int)option)); }}");
            sb.AppendLine($"                SetAttributeValue(\"{logical}\", raw);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            return;
        }

        string? enumType = ResolveEnum(attribute, name, usedNames, localEnums, globals);
        string type = enumType != null ? enumType + "?" : MapType(attribute);

        sb.AppendLine();
        sb.AppendLine($"        /// <summary>{XmlEscape(AttributeDoc(attribute))}</summary>");
        sb.AppendLine($"        [AttributeLogicalName(\"{logical}\")]");
        AppendFacetAttributes(sb, attribute, features);
        if (options.DataAnnotations)
        {
            foreach (string annotation in DataAnnotations(attribute))
            {
                sb.AppendLine($"        {annotation}");
            }
        }

        sb.AppendLine($"        public {type} {name}");
        sb.AppendLine("        {");
        if (enumType != null)
        {
            sb.AppendLine($"            get {{ OptionSetValue raw = GetAttributeValue<OptionSetValue>(\"{logical}\"); return raw == null ? null : ({enumType})raw.Value; }}");
            sb.AppendLine($"            set => SetAttributeValue(\"{logical}\", value.HasValue ? new OptionSetValue((int)value.Value) : null);");
        }
        else if (isPrimaryId)
        {
            // Setting the primary-id property also drives Entity.Id.
            sb.AppendLine($"            get => GetAttributeValue<{type}>(\"{logical}\");");
            sb.AppendLine($"            set {{ SetAttributeValue(\"{logical}\", value); base.Id = value ?? Guid.Empty; }}");
        }
        else
        {
            sb.AppendLine($"            get => GetAttributeValue<{type}>(\"{logical}\");");
            sb.AppendLine($"            set => SetAttributeValue(\"{logical}\", value);");
        }

        sb.AppendLine("        }");
    }

    // Emits a typed factory per alternate key, building an EntityReference from a KeyAttributeCollection so callers
    // can reference/upsert by key without magic strings: e.g. Account.ByAccountNumberKey("A-001").
    private static void AppendAlternateKeys(StringBuilder sb, EntityMetadata entity, HashSet<string> usedNames)
    {
        EntityKeyMetadata[] keys = (entity.Keys ?? Array.Empty<EntityKeyMetadata>())
            .Where(k => k?.KeyAttributes is { Length: > 0 })
            .OrderBy(k => k.LogicalName ?? k.SchemaName, StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, AttributeMetadata> byLogical = (entity.Attributes ?? Array.Empty<AttributeMetadata>())
            .Where(a => !string.IsNullOrWhiteSpace(a?.LogicalName))
            .GroupBy(a => a.LogicalName!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (EntityKeyMetadata key in keys)
        {
            // Keep the "Key" suffix so callers read as ByAccountNumberKey(...). An alternate key can be made of
            // several columns, so each key attribute becomes its own typed parameter, in the key's declared order.
            // Escape AFTER composing so a keyword SchemaName (e.g. "class") yields a valid "ByClass", not "By@class".
            string method = UniqueName(CSharpIdentifier.EscapeKeyword("By" + (key.SchemaName ?? Pascalize(key.LogicalName ?? "Key"))), usedNames);

            HashSet<string> paramNames = new HashSet<string>(StringComparer.Ordinal);
            List<(string LogicalName, string ParamName, string Type)> parts = new List<(string, string, string)>();
            foreach (string attributeLogical in key.KeyAttributes)
            {
                byLogical.TryGetValue(attributeLogical, out AttributeMetadata? attribute);
                string paramType = KeyParamType(attribute);
                string paramName = UniqueName(KeyParamName(attributeLogical, attribute), paramNames);
                parts.Add((attributeLogical, paramName, paramType));
            }

            string signature = string.Join(", ", parts.Select(p => $"{p.Type} {p.ParamName}"));

            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Reference by alternate key (<c>{XmlEscape(string.Join(", ", key.KeyAttributes))}</c>).</summary>");
            sb.AppendLine($"        public static EntityReference {method}({signature})");
            sb.AppendLine("        {");
            sb.AppendLine("            KeyAttributeCollection keys = new KeyAttributeCollection();");
            foreach ((string LogicalName, string ParamName, string Type) part in parts)
            {
                sb.AppendLine($"            keys[\"{part.LogicalName}\"] = {part.ParamName};");
            }

            sb.AppendLine();
            sb.AppendLine("            return new EntityReference(EntityLogicalNameValue, keys);");
            sb.AppendLine("        }");
        }
    }

    // The non-nullable CLR type to accept for an alternate-key column parameter (the value stored in the
    // KeyAttributeCollection). Unknown / unresolved attributes fall back to object so generation never breaks.
    private static string KeyParamType(AttributeMetadata? attribute)
    {
        if (attribute is FileAttributeMetadata)
        {
            return "Guid";
        }

        if (attribute is ImageAttributeMetadata)
        {
            return "byte[]";
        }

        return attribute?.AttributeType switch
        {
            AttributeTypeCode.String or AttributeTypeCode.Memo => "string",
            AttributeTypeCode.Integer => "int",
            AttributeTypeCode.BigInt => "long",
            AttributeTypeCode.Decimal => "decimal",
            AttributeTypeCode.Double => "double",
            AttributeTypeCode.Money => "Money",
            AttributeTypeCode.Boolean => "bool",
            AttributeTypeCode.DateTime => "DateTime",
            AttributeTypeCode.Uniqueidentifier => "Guid",
            // An alternate key on a lookup is keyed on the referenced record's Guid, not the EntityReference, so the
            // factory accepts a Guid (matching how Dataverse indexes the key).
            AttributeTypeCode.Lookup or AttributeTypeCode.Customer or AttributeTypeCode.Owner => "Guid",
            AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status => "OptionSetValue",
            _ => "object",
        };
    }

    private static string KeyParamName(string logicalName, AttributeMetadata? attribute)
    {
        string prop = (attribute?.SchemaName ?? Pascalize(logicalName)).TrimStart('@');
        string camel = string.IsNullOrEmpty(prop) ? logicalName : char.ToLowerInvariant(prop[0]) + prop.Substring(1);
        return CSharpIdentifier.EscapeKeyword(camel);
    }

    // Emits typed relationship navigation properties in the same class file: N:1 (single-target only) as a single
    // typed parent, 1:N as a typed child collection. Each carries [RelationshipSchemaName("...")] (NOT
    // [AttributeLogicalName]) so the querying/image column resolvers and the drift checker never treat it as a
    // column. A relationship whose target table is not in this generation batch is skipped (its class would not
    // exist). Polymorphic / Customer / Owner lookups (a lookup with more than one N:1 relationship) are skipped -
    // they keep their raw EntityReference. N:N is not emitted here (it ships with the read/associate slice).
    private static void AppendRelationships(StringBuilder sb, EntityMetadata entity, AttributeMetadata[] attributes, HashSet<string> usedNames, ISet<string> features, IReadOnlyDictionary<string, string> logicalToClass)
    {
        // N:1 (ManyToOne): group by the referencing (FK) attribute; a lookup with several relationships is
        // polymorphic (Customer/Owner/multi-target) and gets no typed nav.
        var manyToOne = (entity.ManyToOneRelationships ?? Array.Empty<OneToManyRelationshipMetadata>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ReferencingAttribute) && !string.IsNullOrWhiteSpace(r.ReferencedEntity))
            .GroupBy(r => r.ReferencingAttribute, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .Where(r => logicalToClass.ContainsKey(r.ReferencedEntity))
            .OrderBy(r => r.SchemaName, StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, AttributeMetadata> byLogical = attributes
            .Where(a => !string.IsNullOrWhiteSpace(a.LogicalName))
            .GroupBy(a => a.LogicalName!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (OneToManyRelationshipMetadata relationship in manyToOne)
        {
            string targetClass = logicalToClass[relationship.ReferencedEntity];
            // Name the nav after the FK property minus a trailing "Id" (PrimaryContactId -> PrimaryContact); fall
            // back to the relationship schema name when that yields nothing.
            string fkProperty = byLogical.TryGetValue(relationship.ReferencingAttribute, out AttributeMetadata? fk)
                ? PropertyName(fk)
                : Pascalize(relationship.ReferencingAttribute);
            string baseName = StripIdSuffix(fkProperty);
            if (string.IsNullOrEmpty(baseName))
            {
                baseName = Pascalize(relationship.SchemaName);
            }

            string navName = UniqueName(CSharpIdentifier.EscapeKeyword(baseName), usedNames);
            features.Add(RelationshipSchemaNameFeature);

            sb.AppendLine();
            sb.AppendLine($"        /// <summary>N:1 relationship <c>{XmlEscape(relationship.SchemaName)}</c> to <c>{XmlEscape(relationship.ReferencedEntity)}</c>. Populate for a deep insert; hydrated by a relationship-aware read.</summary>");
            sb.AppendLine($"        [RelationshipSchemaName(\"{relationship.SchemaName}\")]");
            sb.AppendLine($"        public {targetClass}? {navName} {{ get; set; }}");
        }

        // 1:N (OneToMany): a typed child collection per relationship whose child table is in the batch.
        var oneToMany = (entity.OneToManyRelationships ?? Array.Empty<OneToManyRelationshipMetadata>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ReferencingEntity) && !string.IsNullOrWhiteSpace(r.SchemaName))
            .Where(r => logicalToClass.ContainsKey(r.ReferencingEntity))
            .OrderBy(r => r.SchemaName, StringComparer.Ordinal)
            .ToArray();

        foreach (OneToManyRelationshipMetadata relationship in oneToMany)
        {
            string childClass = logicalToClass[relationship.ReferencingEntity];
            // Prefer a friendly pluralized child-class name (Contact -> Contacts); when several relationships point
            // at the same child, later ones fall back to the relationship schema name so nothing collides.
            string plural = Pluralize(childClass.TrimStart('@'));
            string navName = usedNames.Contains(plural)
                ? UniqueName(CSharpIdentifier.EscapeKeyword(Pascalize(relationship.SchemaName)), usedNames)
                : UniqueName(CSharpIdentifier.EscapeKeyword(plural), usedNames);
            features.Add(RelationshipSchemaNameFeature);

            sb.AppendLine();
            sb.AppendLine($"        /// <summary>1:N relationship <c>{XmlEscape(relationship.SchemaName)}</c> to <c>{XmlEscape(relationship.ReferencingEntity)}</c>. Add children for a deep insert; hydrated by a relationship-aware read.</summary>");
            sb.AppendLine($"        [RelationshipSchemaName(\"{relationship.SchemaName}\")]");
            sb.AppendLine($"        public System.Collections.Generic.IList<{childClass}> {navName} {{ get; set; }} = new System.Collections.Generic.List<{childClass}>();");
        }

        // N:N (ManyToMany): a typed collection of the OTHER table. Self-referential N:N (both sides this table) are
        // skipped for now (the two directions would need distinct names). Marked ManyToMany = true so the deep-insert
        // projector skips them (they associate existing records, not create) and the associate helper picks them up.
        var manyToMany = (entity.ManyToManyRelationships ?? Array.Empty<ManyToManyRelationshipMetadata>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.SchemaName) &&
                        !string.IsNullOrWhiteSpace(r.Entity1LogicalName) && !string.IsNullOrWhiteSpace(r.Entity2LogicalName))
            .Where(r => !string.Equals(r.Entity1LogicalName, r.Entity2LogicalName, StringComparison.OrdinalIgnoreCase))
            .Select(r => (Relationship: r, Other: string.Equals(r.Entity1LogicalName, entity.LogicalName, StringComparison.OrdinalIgnoreCase) ? r.Entity2LogicalName : r.Entity1LogicalName))
            .Where(x => logicalToClass.ContainsKey(x.Other))
            .OrderBy(x => x.Relationship.SchemaName, StringComparer.Ordinal)
            .ToArray();

        foreach ((ManyToManyRelationshipMetadata relationship, string other) in manyToMany)
        {
            string otherClass = logicalToClass[other];
            string plural = Pluralize(otherClass.TrimStart('@'));
            string navName = usedNames.Contains(plural)
                ? UniqueName(CSharpIdentifier.EscapeKeyword(Pascalize(relationship.SchemaName)), usedNames)
                : UniqueName(CSharpIdentifier.EscapeKeyword(plural), usedNames);
            features.Add(RelationshipSchemaNameFeature);

            sb.AppendLine();
            sb.AppendLine($"        /// <summary>N:N relationship <c>{XmlEscape(relationship.SchemaName)}</c> to <c>{XmlEscape(other)}</c>. Add existing records and associate; hydrated by a relationship-aware read.</summary>");
            sb.AppendLine($"        [RelationshipSchemaName(\"{relationship.SchemaName}\", ManyToMany = true)]");
            sb.AppendLine($"        public System.Collections.Generic.IList<{otherClass}> {navName} {{ get; set; }} = new System.Collections.Generic.List<{otherClass}>();");
        }
    }

    // Drops a trailing "Id"/"id" so a lookup FK property maps to a friendly nav name (PrimaryContactId ->
    // PrimaryContact). Returns the input unchanged when it does not end in Id.
    private static string StripIdSuffix(string name)
    {
        if (name != null && name.Length > 2 && name.EndsWith("Id", StringComparison.Ordinal))
        {
            return name.Substring(0, name.Length - 2);
        }

        return name ?? string.Empty;
    }

    // Naive English pluralizer, enough for deterministic collection nav names (Contact -> Contacts,
    // Opportunity -> Opportunities, Address -> Addresses). Collisions are resolved by the caller.
    private static string Pluralize(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        if (name.EndsWith("y", StringComparison.Ordinal) && name.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(name[name.Length - 2])))
        {
            return name.Substring(0, name.Length - 1) + "ies";
        }

        if (name.EndsWith("s", StringComparison.Ordinal) || name.EndsWith("x", StringComparison.Ordinal) ||
            name.EndsWith("z", StringComparison.Ordinal) || name.EndsWith("ch", StringComparison.Ordinal) ||
            name.EndsWith("sh", StringComparison.Ordinal))
        {
            return name + "es";
        }

        return name + "s";
    }

    // Choice attributes (Picklist/State/Status) with an option set become strongly-typed enums: local sets are
    // nested in the table class, global sets are shared standalone enums. Returns the enum type name, or null to
    // fall back to OptionSetValue.
    private static string? ResolveEnum(AttributeMetadata attribute, string propertyName, HashSet<string> usedNames, List<(string, OptionSetMetadata)> localEnums, IDictionary<string, OptionSetMetadata> globals)
    {
        if (attribute.AttributeType is not (AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status))
        {
            return null;
        }

        if (attribute is not EnumAttributeMetadata em || em.OptionSet?.Options == null || em.OptionSet.Options.Count == 0)
        {
            return null;
        }

        return RegisterEnum(em.OptionSet, propertyName, usedNames, localEnums, globals);
    }

    // Registers an option set as an enum (global → shared/deduped, local → nested) and returns the enum type name.
    // Shared by single choices and multi-select.
    private static string RegisterEnum(OptionSetMetadata optionSet, string propertyName, HashSet<string> usedNames, List<(string, OptionSetMetadata)> localEnums, IDictionary<string, OptionSetMetadata> globals)
    {
        if (optionSet.IsGlobal == true)
        {
            // Dedup by set identity (MetadataId, else name), not by display name: the same global referenced by
            // several tables emits one shared enum file. Reuse the name already assigned to this set.
            foreach (KeyValuePair<string, OptionSetMetadata> existing in globals)
            {
                if (SameOptionSet(existing.Value, optionSet))
                {
                    return existing.Key;
                }
            }

            // A new set: two distinct sets whose names Pascalize to the same identifier are disambiguated with a
            // numeric suffix, so neither is silently dropped nor typed to the other's enum.
            string baseName = CSharpIdentifier.EscapeKeyword(Pascalize(optionSet.Name ?? propertyName.TrimStart('@')));
            string globalName = baseName;
            int suffix = 2;
            while (globals.ContainsKey(globalName))
            {
                globalName = baseName + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            globals[globalName] = optionSet;
            return globalName;
        }

        // Local (nested) enums share the table's declaration space with its properties and methods, so the name
        // must be uniquified against those members (a sibling column named e.g. StatusOptionSet would otherwise
        // collide with the enum → CS0102).
        string localName = UniqueName(propertyName.TrimStart('@') + "OptionSet", usedNames);
        localEnums.Add((localName, optionSet));
        return localName;
    }

    // Two option-set references denote the same global set when they carry the same MetadataId (or, absent an id,
    // the same name); otherwise they are distinct sets that must not share an enum.
    private static bool SameOptionSet(OptionSetMetadata a, OptionSetMetadata b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.MetadataId.HasValue && b.MetadataId.HasValue)
        {
            return a.MetadataId.Value == b.MetadataId.Value;
        }

        return !string.IsNullOrEmpty(a.Name) && string.Equals(a.Name, b.Name, StringComparison.Ordinal);
    }

    private static string GenerateGlobalEnumFile(string name, OptionSetMetadata optionSet, string ns)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Global choice {XmlEscape(LabelText(optionSet.DisplayName) ?? optionSet.Name ?? name)}.</summary>");
        sb.AppendLine($"    public enum {name}");
        sb.AppendLine("    {");
        sb.Append(BuildEnumMembers(optionSet, "        "));
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildEnumMembers(OptionSetMetadata optionSet, string indent)
    {
        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        StringBuilder sb = new StringBuilder();
        foreach (OptionMetadata option in optionSet.Options.OrderBy(o => o.Value ?? 0))
        {
            int value = option.Value ?? 0;
            sb.AppendLine($"{indent}{SanitizeMember(LabelText(option.Label), value, used)} = {value},");
        }

        return sb.ToString();
    }

    private static string SanitizeMember(string? label, int value, HashSet<string> used)
    {
        string filtered = new string((label ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (filtered.Length == 0 || !(char.IsLetter(filtered[0]) || filtered[0] == '_'))
        {
            filtered = "_" + (filtered.Length == 0 ? CSharpIdentifier.NumericToken(value) : filtered);
        }

        string name = CSharpIdentifier.EscapeKeyword(filtered);
        string candidate = name;
        int suffix = value;
        while (!used.Add(candidate))
        {
            candidate = name.TrimStart('@') + "_" + CSharpIdentifier.NumericToken(suffix++);
        }

        return candidate;
    }

    private static string MapType(AttributeMetadata a)
    {
        // File and Image columns have no dedicated AttributeTypeCode in this SDK, so detect them by their
        // metadata subclass (like multi-select picklists). A File column round-trips its `fileid` (a Guid);
        // an Image column round-trips its thumbnail bytes.
        if (a is FileAttributeMetadata)
        {
            return "Guid?";
        }

        if (a is ImageAttributeMetadata)
        {
            return "byte[]";
        }

        return a.AttributeType switch
        {
            AttributeTypeCode.String or AttributeTypeCode.Memo => "string",
            AttributeTypeCode.Integer => "int?",
            AttributeTypeCode.BigInt => "long?",
            AttributeTypeCode.Decimal => "decimal?",
            AttributeTypeCode.Double => "double?",
            AttributeTypeCode.Money => "Money",
            AttributeTypeCode.Boolean => "bool?",
            AttributeTypeCode.DateTime => "DateTime?",
            AttributeTypeCode.Uniqueidentifier => "Guid?",
            AttributeTypeCode.Lookup or AttributeTypeCode.Customer or AttributeTypeCode.Owner => "EntityReference",
            AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status => "OptionSetValue",
            // ActivityParty (partylist, e.g. email From/To/Cc) is an EntityCollection of activityparty rows at
            // runtime. Typed as EntityCollection (faithful + dependency-free) rather than IEnumerable<ActivityParty>,
            // which would require generating the activityparty table.
            AttributeTypeCode.PartyList => "EntityCollection",
            _ => "object",
        };
    }

    // Emits the Vela-generated facet attributes ([RequiredLevel], [LookupTargets]) a property carries. These are
    // not System.ComponentModel annotations — they preserve facets no standard annotation expresses (the full
    // four-value required level; a lookup's target tables) so FakeEntityMetadata.From<T>() can recover them by
    // reflection. Emitted regardless of --no-annotations; each records its feature so the support file is emitted.
    private static void AppendFacetAttributes(StringBuilder sb, AttributeMetadata attribute, ISet<string> features)
    {
        string? requiredLevel = RequiredLevelAnnotation(attribute);
        if (requiredLevel != null)
        {
            sb.AppendLine($"        {requiredLevel}");
            features.Add(RequiredLevelFeature);
        }

        string? lookupTargets = LookupTargetsAnnotation(attribute);
        if (lookupTargets != null)
        {
            sb.AppendLine($"        {lookupTargets}");
            features.Add(LookupTargetsFeature);
        }
    }

    private static string? RequiredLevelAnnotation(AttributeMetadata attribute)
    {
        if (attribute.RequiredLevel?.Value is AttributeRequiredLevel level && level != AttributeRequiredLevel.None)
        {
            return $"[RequiredLevel(Microsoft.Xrm.Sdk.Metadata.AttributeRequiredLevel.{level})]";
        }

        return null;
    }

    private static string? LookupTargetsAnnotation(AttributeMetadata attribute)
    {
        // Only EntityReference-typed lookups: Lookup, Customer and Owner. A polymorphic lookup is simply a lookup
        // with several targets, so it needs no special case. PartyList is a LookupAttributeMetadata too, but maps to
        // EntityCollection and its metadata targets are just "activityparty" (the real party targets live on
        // activityparty.partyid) — so it stays override-only, matching From<T>() skipping partylists.
        if (attribute.AttributeType is not (AttributeTypeCode.Lookup or AttributeTypeCode.Customer or AttributeTypeCode.Owner))
        {
            return null;
        }

        if (attribute is not LookupAttributeMetadata lookup || lookup.Targets is not { Length: > 0 })
        {
            return null;
        }

        string targets = string.Join(", ", lookup.Targets.Select(t => $"\"{t}\""));
        return $"[LookupTargets({targets})]";
    }

    private static string GenerateRequiredLevelAttributeFile(string ns)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine("    using System;");
        sb.AppendLine("    using Microsoft.Xrm.Sdk.Metadata;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Records a column's full Dataverse <see cref=\"AttributeRequiredLevel\"/> (None/Recommended/");
        sb.AppendLine("    /// ApplicationRequired/SystemRequired) on the generated property, preserving the exact level");
        sb.AppendLine("    /// rather than a lossy boolean. Informational: it carries the level for tooling and for");
        sb.AppendLine("    /// <c>FakeEntityMetadata.From&lt;T&gt;()</c> recovery; it does not itself enforce validation.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]");
        sb.AppendLine("    public sealed class RequiredLevelAttribute : Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public RequiredLevelAttribute(AttributeRequiredLevel level)");
        sb.AppendLine("        {");
        sb.AppendLine("            this.Level = level;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public AttributeRequiredLevel Level { get; }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateRelationshipSchemaNameAttributeFile(string ns)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine("    using System;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Marks a generated relationship navigation property (N:1 parent, 1:N child collection, or N:N");
        sb.AppendLine("    /// collection) with its Dataverse relationship schema name. It is NOT a column: the querying and");
        sb.AppendLine("    /// image column resolvers reject a property carrying this attribute (use a relationship-aware");
        sb.AppendLine("    /// read/deep-insert/associate instead). <c>ManyToMany</c> is true for a N:N collection (associate");
        sb.AppendLine("    /// existing records) versus a 1:N collection (deep-insert children).");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]");
        sb.AppendLine("    public sealed class RelationshipSchemaNameAttribute : Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public RelationshipSchemaNameAttribute(string schemaName)");
        sb.AppendLine("        {");
        sb.AppendLine("            this.SchemaName = schemaName ?? string.Empty;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public string SchemaName { get; }");
        sb.AppendLine();
        sb.AppendLine("        public bool ManyToMany { get; set; }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateLookupTargetsAttributeFile(string ns)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine("    using System;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Records the target tables of a lookup / Customer / polymorphic column on the generated");
        sb.AppendLine("    /// property (a single-target lookup carries one; Customer and polymorphic lookups several).");
        sb.AppendLine("    /// Informational: it carries the targets for tooling and for <c>FakeEntityMetadata.From&lt;T&gt;()</c>");
        sb.AppendLine("    /// recovery, since no standard annotation expresses a lookup's targets.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]");
        sb.AppendLine("    public sealed class LookupTargetsAttribute : Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public LookupTargetsAttribute(params string[] targets)");
        sb.AppendLine("        {");
        sb.AppendLine("            this.Targets = targets ?? Array.Empty<string>();");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public string[] Targets { get; }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static IEnumerable<string> DataAnnotations(AttributeMetadata attribute)
    {
        if (attribute is StringAttributeMetadata s && s.MaxLength.HasValue)
        {
            yield return $"[StringLength({s.MaxLength.Value})]";
        }
        else if (attribute is MemoAttributeMetadata m && m.MaxLength.HasValue)
        {
            yield return $"[StringLength({m.MaxLength.Value})]";
        }

        // [Range] operand type must match the property so DataAnnotations validation does not throw / lose precision:
        //  - int/double validate cleanly against the double-literal overload.
        //  - decimal needs the typed overload [Range(typeof(decimal), "min", "max")]; the double-literal overload
        //    validates through double and loses precision (#308).
        //  - Money (Microsoft.Xrm.Sdk.Money) is not convertible by RangeAttribute at all, so a [Range] would throw
        //    InvalidCastException at validation time; emit none.
        switch (attribute)
        {
            case IntegerAttributeMetadata i when i.MinValue.HasValue && i.MaxValue.HasValue:
                yield return $"[Range({i.MinValue.Value}, {i.MaxValue.Value})]";
                break;
            case DoubleAttributeMetadata o when o.MinValue.HasValue && o.MaxValue.HasValue:
                yield return $"[Range({o.MinValue.Value.ToString(CultureInfo.InvariantCulture)}, {o.MaxValue.Value.ToString(CultureInfo.InvariantCulture)})]";
                break;
            case DecimalAttributeMetadata d when d.MinValue.HasValue && d.MaxValue.HasValue:
                yield return $"[Range(typeof(decimal), \"{d.MinValue.Value.ToString(CultureInfo.InvariantCulture)}\", \"{d.MaxValue.Value.ToString(CultureInfo.InvariantCulture)}\")]";
                break;
        }
    }

    // The numeric min/max as doubles, for the human-readable range shown in the property's XML doc comment only
    // (the [Range] data annotation is emitted separately with a type-correct operand; see DataAnnotations).
    private static (double? min, double? max) NumericRange(AttributeMetadata a) => a switch
    {
        IntegerAttributeMetadata i => (i.MinValue, i.MaxValue),
        DecimalAttributeMetadata d => ((double?)d.MinValue, (double?)d.MaxValue),
        DoubleAttributeMetadata o => (o.MinValue, o.MaxValue),
        MoneyAttributeMetadata mo => (mo.MinValue, mo.MaxValue),
        _ => (null, null),
    };

    private static string EntityDoc(EntityMetadata entity)
    {
        string display = LabelText(entity.DisplayName) ?? entity.SchemaName ?? entity.LogicalName;
        string? description = LabelText(entity.Description);
        return string.IsNullOrWhiteSpace(description) ? display : $"{display} — {description}";
    }

    private static string AttributeDoc(AttributeMetadata attribute)
    {
        List<string> parts = new List<string>();
        string display = LabelText(attribute.DisplayName) ?? attribute.SchemaName ?? attribute.LogicalName;
        parts.Add(display);

        string? description = LabelText(attribute.Description);
        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add(description!);
        }

        List<string> constraints = new List<string>();
        int? maxLength = (attribute as StringAttributeMetadata)?.MaxLength ?? (attribute as MemoAttributeMetadata)?.MaxLength;
        if (maxLength.HasValue)
        {
            constraints.Add($"max {maxLength.Value}");
        }

        (double? min, double? max) = NumericRange(attribute);
        if (min.HasValue && max.HasValue)
        {
            constraints.Add($"{min.Value.ToString(CultureInfo.InvariantCulture)}–{max.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (attribute is LookupAttributeMetadata lookup && lookup.Targets is { Length: > 0 })
        {
            constraints.Add("Targets: " + string.Join(", ", lookup.Targets));
        }

        if (attribute.RequiredLevel?.Value is AttributeRequiredLevel level && level != AttributeRequiredLevel.None)
        {
            constraints.Add($"Required: {level}");
        }

        return constraints.Count == 0
            ? string.Join(" — ", parts)
            : string.Join(" — ", parts) + $" ({string.Join("; ", constraints)})";
    }

    private static string ClassName(EntityMetadata entity) =>
        CSharpIdentifier.EscapeKeyword(entity.SchemaName ?? Pascalize(entity.LogicalName));

    private static string PropertyName(AttributeMetadata attribute) =>
        CSharpIdentifier.EscapeKeyword(attribute.SchemaName ?? Pascalize(attribute.LogicalName));

    private static string UniqueName(string name, HashSet<string> used)
    {
        string candidate = name;
        int suffix = 1;
        while (!used.Add(candidate))
        {
            candidate = name.TrimStart('@') + suffix++;
        }

        return candidate;
    }

    private static string Pascalize(string logicalName)
    {
        if (string.IsNullOrEmpty(logicalName))
        {
            return logicalName;
        }

        return char.ToUpperInvariant(logicalName[0]) + logicalName.Substring(1);
    }

    // Server metadata fills UserLocalizedLabel; client-built labels only fill LocalizedLabels — read either.
    private static string? LabelText(Microsoft.Xrm.Sdk.Label? label) =>
        label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels?.FirstOrDefault()?.Label;

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            // Collapse newlines: labels/descriptions are emitted into a single-line /// <summary> and a raw CR/LF
            // would leave the continuation without a /// prefix, producing uncompilable generated code.
            .Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
}
