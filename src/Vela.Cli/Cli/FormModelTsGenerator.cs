using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Vela.Cli;

/// <summary>
/// A form to generate a TS model for: the systemform header (<see cref="FormId"/>, <see cref="FormType"/>,
/// <see cref="FormName"/>), its parsed <see cref="ParsedForm"/> layout, and the owning table's metadata.
/// </summary>
public sealed record FormSource(string FormId, string FormType, string FormName, ParsedForm Form, EntityMetadata Entity);

/// <summary>
/// Pure TypeScript form-model emitter: the FormXML analog of <see cref="ModelGenerator"/>. Turns a set of
/// <see cref="FormSource"/> into the <c>.ts</c> files a vela-client runtime consumes - one
/// <c>&lt;entity&gt;.&lt;form&gt;.form.ts</c> const per form (<c>satisfies FormModel</c>) plus a shared
/// <c>option-sets.ts</c> of numeric enums. No Dataverse I/O: feed it parsed layouts + metadata and it returns the
/// files to write. Deterministic - fields follow the form's layout order and option sets follow first-reference order.
/// </summary>
public static class FormModelTsGenerator
{
    private const string ClientPackage = "vela-client";
    private const string OptionSetsModule = "./option-sets";
    private const string OptionSetsFileName = "option-sets.ts";

    public static IReadOnlyList<GeneratedModel> Generate(IEnumerable<FormSource> forms)
    {
        if (forms == null)
        {
            throw new ArgumentNullException(nameof(forms));
        }

        // Option sets are shared across forms → registered once (keyed by their deterministic enum name), in
        // first-reference order, and emitted into a single option-sets.ts. Mirrors how ModelGenerator dedupes
        // global option sets, but order is kept explicit so the output is stable regardless of dictionary internals.
        OptionSetRegistry optionSets = new OptionSetRegistry();

        // Several forms of the same table can slug to the same const/file name (e.g. a Main and a Quick Create form
        // both named "Account"). Disambiguate deterministically up front so no generated file is silently
        // overwritten by a later one in the batch.
        HashSet<string> usedConsts = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<GeneratedModel> files = new List<GeneratedModel>();
        foreach (FormSource form in forms.Where(f => f?.Entity?.LogicalName is { Length: > 0 }))
        {
            string entity = form.Entity.LogicalName;
            string constName = UniqueName(ConstName(entity, form.FormName), usedConsts);
            string fileName = UniqueFileName(FileName(entity, form.FormName), usedFiles);
            files.Add(GenerateForm(form, entity, constName, fileName, optionSets));
        }

        if (optionSets.Count > 0)
        {
            files.Add(new GeneratedModel(OptionSetsFileName, GenerateOptionSetsFile(optionSets)));
        }

        return files;
    }

    // Insertion-ordered set of option sets keyed by enum name, so the shared option-sets.ts is deterministic.
    private sealed class OptionSetRegistry
    {
        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<(string Name, OptionSetMetadata Set)> _ordered = new List<(string, OptionSetMetadata)>();

        public int Count => _ordered.Count;

        public IReadOnlyList<(string Name, OptionSetMetadata Set)> Entries => _ordered;

        public void Register(string name, OptionSetMetadata set)
        {
            if (_names.Add(name))
            {
                _ordered.Add((name, set));
            }
        }
    }

    private static GeneratedModel GenerateForm(FormSource source, string entity, string constName, string fileName, OptionSetRegistry optionSets)
    {
        IReadOnlyDictionary<string, AttributeMetadata> attributes = source.Entity.Attributes?
            .Where(a => !string.IsNullOrWhiteSpace(a?.LogicalName))
            .GroupBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);

        // Resolve every field cell to metadata in layout order (body first, then header/footer), keeping the first
        // occurrence of each column. A cell whose column is not in the metadata is dropped - it can't be typed.
        List<PlacedField> placed = new List<PlacedField>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FormTab tab in source.Form.Tabs)
        {
            foreach (FormSection section in tab.Sections)
            {
                foreach (FormFieldCell cell in section.Cells)
                {
                    TryPlace(entity, cell, new FieldLocation(tab.Name, section.Name), attributes, seen, placed);
                }
            }
        }

        foreach (FormFieldCell cell in source.Form.HeaderFields.Concat(source.Form.FooterFields))
        {
            TryPlace(entity, cell, null, attributes, seen, placed);
        }

        StringBuilder sb = new StringBuilder();
        sb.Append("// generated, do not edit\n");
        sb.Append($"import type {{ FormModel }} from '{ClientPackage}';\n");

        string[] referencedEnums = placed
            .Select(p => p.OptionSetEnum)
            .Where(name => name != null)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (referencedEnums.Length > 0)
        {
            sb.Append($"import {{ {string.Join(", ", referencedEnums)} }} from '{OptionSetsModule}';\n");
        }

        sb.Append('\n');
        sb.Append($"export const {constName} = {{\n");
        sb.Append($"  entity: {TsString(entity)},\n");
        sb.Append($"  formId: {TsString(source.FormId)},\n");
        sb.Append($"  formType: {TsString(source.FormType)},\n");

        AppendFields(sb, placed);
        AppendLayout(sb, source.Form, placed);
        AppendSubgrids(sb, source.Form.Subgrids);

        // Register the referenced option sets in placement order, so the shared option-sets.ts is deterministic.
        foreach (PlacedField field in placed.Where(p => p.OptionSet != null))
        {
            optionSets.Register(field.OptionSetEnum!, field.OptionSet!);
        }

        sb.Append("} as const satisfies FormModel;\n");
        return new GeneratedModel(fileName, sb.ToString());
    }

    private static void TryPlace(string entity, FormFieldCell cell, FieldLocation? location, IReadOnlyDictionary<string, AttributeMetadata> attributes, HashSet<string> seen, List<PlacedField> placed)
    {
        if (!seen.Add(cell.DataFieldName) || !attributes.TryGetValue(cell.DataFieldName, out AttributeMetadata? attribute))
        {
            return;
        }

        placed.Add(PlacedField.Create(entity, cell, location, attribute));
    }

    private static void AppendFields(StringBuilder sb, IReadOnlyList<PlacedField> placed)
    {
        sb.Append("  fields: {\n");
        foreach (PlacedField field in placed)
        {
            sb.Append($"    {PropertyKey(field.LogicalName)}: {{\n");
            sb.Append($"      kind: {TsString(field.Kind)},\n");
            sb.Append($"      label: {TsString(field.Label)},\n");
            sb.Append($"      required: {TsString(field.Required)},\n");
            foreach (string line in field.TypeSpecificLines)
            {
                sb.Append($"      {line}\n");
            }

            if (field.Location != null)
            {
                sb.Append($"      at: {{ tab: {TsString(field.Location.Tab)}, section: {TsString(field.Location.Section)} }},\n");
            }

            sb.Append("    },\n");
        }

        sb.Append("  },\n");
    }

    private static void AppendLayout(StringBuilder sb, ParsedForm form, IReadOnlyList<PlacedField> placed)
    {
        HashSet<string> placedNames = new HashSet<string>(placed.Where(p => p.Location != null).Select(p => p.LogicalName), StringComparer.OrdinalIgnoreCase);

        sb.Append("  layout: {\n");
        sb.Append("    tabs: {\n");
        foreach (FormTab tab in form.Tabs)
        {
            // Only sections holding at least one emitted field make the layout (empty/structural sections are noise).
            List<FormSection> sections = tab.Sections
                .Where(s => s.Cells.Any(c => placedNames.Contains(c.DataFieldName)))
                .ToList();
            if (sections.Count == 0)
            {
                continue;
            }

            sb.Append($"      {PropertyKey(tab.Name)}: {{\n");
            sb.Append($"        label: {TsString(tab.Label ?? tab.Name)},\n");
            sb.Append("        sections: {\n");
            foreach (FormSection section in sections)
            {
                string[] fields = section.Cells
                    .Select(c => c.DataFieldName)
                    .Where(placedNames.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                sb.Append($"          {PropertyKey(section.Name)}: {{\n");
                sb.Append($"            label: {TsString(section.Label ?? section.Name)},\n");
                AppendFieldList(sb, fields);
                sb.Append("          },\n");
            }

            sb.Append("        },\n");
            sb.Append("      },\n");
        }

        sb.Append("    },\n");
        sb.Append("  },\n");
    }

    // A single field folds onto one line (`fields: ['x']`); several are laid out one per line, matching the
    // hand-written contract fixtures.
    private static void AppendFieldList(StringBuilder sb, IReadOnlyList<string> fields)
    {
        if (fields.Count <= 1)
        {
            string only = fields.Count == 0 ? string.Empty : TsString(fields[0]);
            sb.Append($"            fields: [{only}],\n");
            return;
        }

        sb.Append("            fields: [\n");
        foreach (string field in fields)
        {
            sb.Append($"              {TsString(field)},\n");
        }

        sb.Append("            ],\n");
    }

    private static void AppendSubgrids(StringBuilder sb, IReadOnlyList<FormSubgrid> subgrids)
    {
        if (subgrids.Count == 0)
        {
            return;
        }

        sb.Append("  subgrids: {\n");
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FormSubgrid subgrid in subgrids)
        {
            // Subgrid names key an object; skip a duplicate (e.g. two grids whose name falls back to the same
            // target) so the emitted `as const` never carries a duplicate key.
            if (!seen.Add(subgrid.Name))
            {
                continue;
            }

            StringBuilder inner = new StringBuilder();
            inner.Append($"entity: {TsString(subgrid.TargetEntity)}");
            if (!string.IsNullOrWhiteSpace(subgrid.RelationshipName))
            {
                inner.Append($", relationship: {TsString(subgrid.RelationshipName!)}");
            }

            if (!string.IsNullOrWhiteSpace(subgrid.ViewId))
            {
                inner.Append($", viewId: {TsString(subgrid.ViewId!)}");
            }

            sb.Append($"    {PropertyKey(subgrid.Name)}: {{ {inner} }},\n");
        }

        sb.Append("  },\n");
    }

    private static string GenerateOptionSetsFile(OptionSetRegistry optionSets)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("// generated, do not edit\n");
        foreach ((string name, OptionSetMetadata set) in optionSets.Entries)
        {
            sb.Append('\n');
            sb.Append($"export enum {name} {{\n");
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            foreach (OptionMetadata option in set.Options.OrderBy(o => o.Value ?? 0))
            {
                int value = option.Value ?? 0;
                sb.Append($"  {SanitizeMember(LabelText(option.Label), value, used)} = {value.ToString(CultureInfo.InvariantCulture)},\n");
            }

            sb.Append("}\n");
        }

        return sb.ToString();
    }

    // --- field model -------------------------------------------------------------------------------------------

    private sealed record FieldLocation(string Tab, string Section);

    private sealed record PlacedField(
        string LogicalName,
        string Kind,
        string Label,
        string Required,
        IReadOnlyList<string> TypeSpecificLines,
        FieldLocation? Location,
        string? OptionSetEnum,
        OptionSetMetadata? OptionSet)
    {
        public static PlacedField Create(string entity, FormFieldCell cell, FieldLocation? location, AttributeMetadata attribute)
        {
            string kind = MapKind(attribute);
            string label = cell.Label ?? LabelText(attribute.DisplayName) ?? attribute.SchemaName ?? attribute.LogicalName;
            string required = ResolveRequired(cell, attribute);

            List<string> lines = new List<string>();
            string? enumName = null;
            OptionSetMetadata? optionSet = null;

            switch (kind)
            {
                case "string":
                case "memo":
                    AppendStringFacets(attribute, lines);
                    break;
                case "integer":
                case "bigint":
                case "decimal":
                case "double":
                case "money":
                    AppendNumberFacets(attribute, lines);
                    break;
                case "date":
                    string? dateFormat = DateFormat(attribute);
                    if (dateFormat != null)
                    {
                        lines.Add($"format: {TsString(dateFormat)},");
                    }

                    break;
                case "choice":
                case "multichoice":
                    optionSet = OptionSetOf(attribute);
                    if (optionSet != null)
                    {
                        enumName = EnumName(entity, attribute, optionSet);
                        lines.Add($"options: {enumName},");
                    }

                    break;
                case "lookup":
                    string[] targets = LookupTargets(attribute);
                    if (targets.Length > 0)
                    {
                        lines.Add($"targets: [{string.Join(", ", targets.Select(TsString))}],");
                    }

                    if (attribute.AttributeType == AttributeTypeCode.PartyList)
                    {
                        lines.Add("partyList: true,");
                    }

                    break;
            }

            return new PlacedField(attribute.LogicalName, kind, label, required, lines, location, enumName, optionSet);
        }
    }

    private static void AppendStringFacets(AttributeMetadata attribute, List<string> lines)
    {
        int? maxLength = (attribute as StringAttributeMetadata)?.MaxLength ?? (attribute as MemoAttributeMetadata)?.MaxLength;
        if (maxLength.HasValue)
        {
            lines.Add($"maxLength: {maxLength.Value.ToString(CultureInfo.InvariantCulture)},");
        }

        string? format = StringFormatName(attribute);
        if (format != null)
        {
            lines.Add($"format: {TsString(format)},");
        }
    }

    private static void AppendNumberFacets(AttributeMetadata attribute, List<string> lines)
    {
        (double? min, double? max, int? precision) = NumberFacets(attribute);
        if (min.HasValue)
        {
            lines.Add($"min: {NumberLiteral(min.Value)},");
        }

        if (max.HasValue)
        {
            lines.Add($"max: {NumberLiteral(max.Value)},");
        }

        if (precision.HasValue)
        {
            lines.Add($"precision: {precision.Value.ToString(CultureInfo.InvariantCulture)},");
        }
    }

    // --- metadata mapping (mirrors ModelGenerator.MapType, projected onto the FormModel field kinds) -------------

    private static string MapKind(AttributeMetadata a)
    {
        if (a is MultiSelectPicklistAttributeMetadata)
        {
            return "multichoice";
        }

        if (a is FileAttributeMetadata)
        {
            return "file";
        }

        if (a is ImageAttributeMetadata)
        {
            return "image";
        }

        return a.AttributeType switch
        {
            AttributeTypeCode.String => "string",
            AttributeTypeCode.Memo => "memo",
            AttributeTypeCode.Integer => "integer",
            AttributeTypeCode.BigInt => "bigint",
            AttributeTypeCode.Decimal => "decimal",
            AttributeTypeCode.Double => "double",
            AttributeTypeCode.Money => "money",
            AttributeTypeCode.Boolean => "boolean",
            AttributeTypeCode.DateTime => "date",
            AttributeTypeCode.Uniqueidentifier => "guid",
            AttributeTypeCode.Lookup or AttributeTypeCode.Customer or AttributeTypeCode.Owner or AttributeTypeCode.PartyList => "lookup",
            AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status => "choice",
            _ => "string",
        };
    }

    private static (double? Min, double? Max, int? Precision) NumberFacets(AttributeMetadata a) => a switch
    {
        IntegerAttributeMetadata i => (i.MinValue, i.MaxValue, null),
        BigIntAttributeMetadata b => (b.MinValue, b.MaxValue, null),
        DecimalAttributeMetadata d => ((double?)d.MinValue, (double?)d.MaxValue, d.Precision),
        DoubleAttributeMetadata o => (o.MinValue, o.MaxValue, o.Precision),
        MoneyAttributeMetadata m => (m.MinValue, m.MaxValue, m.Precision),
        _ => (null, null, null),
    };

    private static string? StringFormatName(AttributeMetadata a)
    {
        StringFormat? format = (a as StringAttributeMetadata)?.Format ?? (a as MemoAttributeMetadata)?.Format;
        return format switch
        {
            StringFormat.Email => "email",
            StringFormat.Url => "url",
            StringFormat.Phone => "phone",
            StringFormat.TickerSymbol => "ticker",
            StringFormat.TextArea => "textarea",
            StringFormat.Text => "text",
            _ => null,
        };
    }

    private static string? DateFormat(AttributeMetadata a) => (a as DateTimeAttributeMetadata)?.Format switch
    {
        DateTimeFormat.DateOnly => "dateonly",
        DateTimeFormat.DateAndTime => "datetime",
        _ => null,
    };

    private static string[] LookupTargets(AttributeMetadata a) =>
        (a as LookupAttributeMetadata)?.Targets ?? Array.Empty<string>();

    private static OptionSetMetadata? OptionSetOf(AttributeMetadata a) =>
        (a as EnumAttributeMetadata)?.OptionSet is { Options.Count: > 0 } set ? set : null;

    // Local option sets are named <entity>_<column> (the vela-client fixture convention); a global set keeps its own
    // (sanitized) name so it is shared across the tables that reference it.
    private static string EnumName(string entity, AttributeMetadata attribute, OptionSetMetadata optionSet)
    {
        if (optionSet.IsGlobal == true && !string.IsNullOrWhiteSpace(optionSet.Name))
        {
            return SanitizeIdentifier(optionSet.Name!);
        }

        return SanitizeIdentifier($"{entity}_{attribute.LogicalName}");
    }

    private static string ResolveRequired(FormFieldCell cell, AttributeMetadata attribute)
    {
        if (cell.Required.HasValue)
        {
            return cell.Required.Value ? "required" : "none";
        }

        return attribute.RequiredLevel?.Value switch
        {
            AttributeRequiredLevel.ApplicationRequired or AttributeRequiredLevel.SystemRequired => "required",
            AttributeRequiredLevel.Recommended => "recommended",
            _ => "none",
        };
    }

    // --- emission helpers --------------------------------------------------------------------------------------

    private static string ConstName(string entity, string formName) => Pascalize(entity) + Pascalize(formName);

    private static string FileName(string entity, string formName) => $"{entity}.{Slug(formName)}.form.ts";

    // Ensures a name is unique within the batch by appending 2, 3, ... on collision (matching ModelGenerator).
    private static string UniqueName(string name, HashSet<string> used)
    {
        string candidate = name;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = name + suffix++;
        }

        return candidate;
    }

    // Like UniqueName but keeps the ".form.ts" suffix intact, inserting -2, -3, ... before it on collision.
    private static string UniqueFileName(string fileName, HashSet<string> used)
    {
        const string ext = ".form.ts";
        string stem = fileName.EndsWith(ext, StringComparison.Ordinal) ? fileName[..^ext.Length] : fileName;
        string candidate = fileName;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{stem}-{suffix++}{ext}";
        }

        return candidate;
    }

    private static string Slug(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Pascalize(string value)
    {
        IEnumerable<string> words = value.Split(new[] { ' ', '_', '-', '.', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
    }

    private static string PropertyKey(string name) => IsIdentifier(name) ? name : TsString(name);

    private static bool IsIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_' || name[0] == '$'))
        {
            return false;
        }

        return name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '$');
    }

    private static string SanitizeIdentifier(string name)
    {
        string filtered = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (filtered.Length == 0 || char.IsDigit(filtered[0]))
        {
            filtered = "_" + filtered;
        }

        return filtered;
    }

    private static string SanitizeMember(string? label, int value, HashSet<string> used)
    {
        string filtered = new string((label ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (filtered.Length == 0 || !(char.IsLetter(filtered[0]) || filtered[0] == '_'))
        {
            filtered = "_" + (filtered.Length == 0 ? value.ToString(CultureInfo.InvariantCulture) : filtered);
        }

        string candidate = filtered;
        int suffix = value;
        while (!used.Add(candidate))
        {
            candidate = filtered + "_" + suffix++;
        }

        return candidate;
    }

    // Shortest round-tripping literal: integral values lose the trailing ".0".
    private static string NumberLiteral(double value) =>
        value == Math.Floor(value) && !double.IsInfinity(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    // Emits a single-quoted TS string literal, escaping the backslash, the quote and every character that would
    // otherwise break or terminate the literal (newlines, CR, tab, and the JS line separators U+2028/U+2029).
    private static string TsString(string value)
    {
        StringBuilder sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('\'');
        return sb.ToString();
    }

    // Server metadata fills UserLocalizedLabel; client-built labels only fill LocalizedLabels - read either.
    private static string? LabelText(Label? label) =>
        label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels?.FirstOrDefault()?.Label;
}
