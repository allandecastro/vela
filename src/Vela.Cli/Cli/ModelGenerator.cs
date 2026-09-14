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
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

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

        List<GeneratedModel> files = entities
            .Where(e => !string.IsNullOrWhiteSpace(e?.LogicalName))
            .OrderBy(e => e.LogicalName, StringComparer.Ordinal)
            .Select(e => new GeneratedModel($"{ClassName(e)}.cs", GenerateOne(e, options, globals, features)))
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

        return files;
    }

    private const string RequiredLevelFeature = "requiredLevel";
    private const string LookupTargetsFeature = "lookupTargets";

    private static string GenerateOne(EntityMetadata entity, ModelGenOptions options, IDictionary<string, OptionSetMetadata> globals, ISet<string> features)
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
            string msEnum = RegisterEnum(multi.OptionSet, name, localEnums, globals);
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

        string? enumType = ResolveEnum(attribute, name, localEnums, globals);
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

        foreach (EntityKeyMetadata key in keys)
        {
            string method = UniqueName("By" + Escape(key.SchemaName ?? Pascalize(key.LogicalName ?? "Key")), usedNames);
            string attributes = string.Join(", ", key.KeyAttributes.Select(a => $"\"{a}\""));

            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Reference by alternate key (<c>{XmlEscape(string.Join(", ", key.KeyAttributes))}</c>). Pass the key values in order.</summary>");
            sb.AppendLine($"        public static EntityReference {method}(params object[] keyValues)");
            sb.AppendLine("        {");
            sb.AppendLine($"            string[] attributes = {{ {attributes} }};");
            sb.AppendLine("            KeyAttributeCollection keys = new KeyAttributeCollection();");
            sb.AppendLine("            for (int i = 0; i < attributes.Length && i < keyValues.Length; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                keys[attributes[i]] = keyValues[i];");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            return new EntityReference(EntityLogicalNameValue, keys);");
            sb.AppendLine("        }");
        }
    }

    // Choice attributes (Picklist/State/Status) with an option set become strongly-typed enums: local sets are
    // nested in the table class, global sets are shared standalone enums. Returns the enum type name, or null to
    // fall back to OptionSetValue.
    private static string? ResolveEnum(AttributeMetadata attribute, string propertyName, List<(string, OptionSetMetadata)> localEnums, IDictionary<string, OptionSetMetadata> globals)
    {
        if (attribute.AttributeType is not (AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status))
        {
            return null;
        }

        if (attribute is not EnumAttributeMetadata em || em.OptionSet?.Options == null || em.OptionSet.Options.Count == 0)
        {
            return null;
        }

        return RegisterEnum(em.OptionSet, propertyName, localEnums, globals);
    }

    // Registers an option set as an enum (global → shared/deduped, local → nested) and returns the enum type name.
    // Shared by single choices and multi-select.
    private static string RegisterEnum(OptionSetMetadata optionSet, string propertyName, List<(string, OptionSetMetadata)> localEnums, IDictionary<string, OptionSetMetadata> globals)
    {
        if (optionSet.IsGlobal == true)
        {
            string globalName = Escape(Pascalize(optionSet.Name ?? propertyName.TrimStart('@')));
            if (!globals.ContainsKey(globalName))
            {
                globals[globalName] = optionSet;
            }

            return globalName;
        }

        string localName = propertyName.TrimStart('@') + "OptionSet";
        localEnums.Add((localName, optionSet));
        return localName;
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

        string name = Keywords.Contains(filtered) ? "@" + filtered : filtered;
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

        (double? min, double? max) = NumericRange(attribute);
        if (min.HasValue && max.HasValue)
        {
            string lo = min.Value.ToString(CultureInfo.InvariantCulture);
            string hi = max.Value.ToString(CultureInfo.InvariantCulture);
            yield return $"[Range({lo}, {hi})]";
        }
    }

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
        Escape(entity.SchemaName ?? Pascalize(entity.LogicalName));

    private static string PropertyName(AttributeMetadata attribute) =>
        Escape(attribute.SchemaName ?? Pascalize(attribute.LogicalName));

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

    private static string Escape(string identifier) =>
        Keywords.Contains(identifier) ? "@" + identifier : identifier;

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
