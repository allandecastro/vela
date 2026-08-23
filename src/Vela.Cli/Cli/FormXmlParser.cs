using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Vela.Cli;

/// <summary>A field cell on a form: its bound column plus any form-level label / requirement override.</summary>
public sealed record FormFieldCell(string DataFieldName, string? Label, bool? Required);

/// <summary>A section within a tab: its name, label and the field cells laid out in it (ordinal order).</summary>
public sealed record FormSection(string Name, string? Label, IReadOnlyList<FormFieldCell> Cells);

/// <summary>A tab: its name, label and sections (ordinal order).</summary>
public sealed record FormTab(string Name, string? Label, IReadOnlyList<FormSection> Sections);

/// <summary>A subgrid control: an opaque related-records container. In-cell controls are not addressable.</summary>
public sealed record FormSubgrid(string Name, string TargetEntity, string? RelationshipName, string? ViewId);

/// <summary>
/// The parsed shape of a form's FormXML: the tab/section/field tree, its subgrids, and the header/footer field
/// cells (which sit outside the tab tree, so they carry no location). Purely structural - no metadata is resolved
/// here; the generator joins this with <see cref="Microsoft.Xrm.Sdk.Metadata.EntityMetadata"/>.
/// </summary>
public sealed record ParsedForm(
    IReadOnlyList<FormTab> Tabs,
    IReadOnlyList<FormSubgrid> Subgrids,
    IReadOnlyList<FormFieldCell> HeaderFields,
    IReadOnlyList<FormFieldCell> FooterFields);

/// <summary>
/// Pure FormXML reader: turns a form's <c>formxml</c> into a <see cref="ParsedForm"/> tree. No Dataverse I/O and no
/// metadata resolution - feed it the XML string and it returns the structure. Tolerant of the XML namespace (matches
/// on local names) and of the two well-known subgrid class ids. Ordering is preserved as authored in the XML so the
/// generated model is deterministic.
/// </summary>
public static class FormXmlParser
{
    // The subgrid control class id (a related-records grid). Braces/casing vary in the wild, so it is compared
    // case-insensitively with braces stripped.
    private const string SubgridClassId = "E7A81278-8635-4d9e-8D4D-59480B391C5B";

    private const uint BaseLanguageCode = 1033;

    public static ParsedForm Parse(string formXml)
    {
        if (string.IsNullOrWhiteSpace(formXml))
        {
            throw new ArgumentException("FormXML is empty.", nameof(formXml));
        }

        XElement root = XDocument.Parse(formXml).Root
            ?? throw new ArgumentException("FormXML has no root element.", nameof(formXml));

        List<FormTab> tabs = new List<FormTab>();
        List<FormSubgrid> subgrids = new List<FormSubgrid>();

        foreach (XElement tab in Descend(root, "tabs").SelectMany(t => Children(t, "tab")))
        {
            List<FormSection> sections = new List<FormSection>();

            // A tab lays sections out across columns; flatten the columns in document order.
            IEnumerable<XElement> sectionElements = Children(tab, "columns")
                .SelectMany(c => Children(c, "column"))
                .SelectMany(c => Descend(c, "sections"))
                .SelectMany(s => Children(s, "section"));

            foreach (XElement section in sectionElements)
            {
                List<FormFieldCell> cells = new List<FormFieldCell>();
                foreach (XElement cell in CellsOf(section))
                {
                    ClassifyCell(cell, cells, subgrids);
                }

                sections.Add(new FormSection(Attr(section, "name") ?? string.Empty, LabelOf(section), cells));
            }

            tabs.Add(new FormTab(Attr(tab, "name") ?? string.Empty, LabelOf(tab), sections));
        }

        return new ParsedForm(tabs, subgrids, RegionFields(root, "header"), RegionFields(root, "footer"));
    }

    // Header/footer fields sit outside the tab tree; collect just their bound field cells (subgrids there are rare
    // and, like body subgrids, are routed to the subgrid list).
    private static IReadOnlyList<FormFieldCell> RegionFields(XElement root, string region)
    {
        List<FormFieldCell> cells = new List<FormFieldCell>();
        List<FormSubgrid> ignored = new List<FormSubgrid>();
        foreach (XElement regionElement in Children(root, region))
        {
            foreach (XElement cell in CellsOf(regionElement))
            {
                ClassifyCell(cell, cells, ignored);
            }
        }

        return cells;
    }

    // A cell is either a bound field (has a control with a datafieldname), a subgrid (subgrid class id or a
    // TargetEntityType parameter), or structural noise (spacers, web-resource/iframe controls) - which is skipped.
    private static void ClassifyCell(XElement cell, List<FormFieldCell> fields, List<FormSubgrid> subgrids)
    {
        XElement? control = Children(cell, "control").FirstOrDefault();
        if (control == null)
        {
            return;
        }

        FormSubgrid? subgrid = TryReadSubgrid(control);
        if (subgrid != null)
        {
            subgrids.Add(subgrid);
            return;
        }

        string? dataField = Attr(control, "datafieldname");
        if (string.IsNullOrWhiteSpace(dataField))
        {
            return;
        }

        // The field label lives on the cell; the control may carry its own (used as a fallback).
        fields.Add(new FormFieldCell(dataField!, LabelOf(cell) ?? LabelOf(control), ReadRequired(control)));
    }

    private static FormSubgrid? TryReadSubgrid(XElement control)
    {
        XElement? parameters = Children(control, "parameters").FirstOrDefault();
        string? targetEntity = parameters == null ? null : ChildValue(parameters, "TargetEntityType");

        // A subgrid is marked by the subgrid class id, an explicit indicationOfSubgrid flag, or (as a fallback)
        // a TargetEntityType parameter - but a real target is required to type it.
        bool isSubgrid = EqualsClassId(Attr(control, "classid"), SubgridClassId)
            || string.Equals(Attr(control, "indicationOfSubgrid"), "true", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(targetEntity);
        if (!isSubgrid || string.IsNullOrWhiteSpace(targetEntity))
        {
            return null;
        }

        string name = Attr(control, "id") ?? Attr(control, "uniqueid") ?? targetEntity!;
        string? relationship = NullIfEmpty(parameters == null ? null : ChildValue(parameters, "RelationshipName"))
            ?? NullIfEmpty(Attr(control, "relationship"));
        string? viewId = parameters == null ? null : NormalizeGuid(ChildValue(parameters, "ViewId"));
        return new FormSubgrid(name, targetEntity!, relationship, viewId);
    }

    // A control may pin its field required via `isrequired` (a form-level override of the column's metadata level).
    private static bool? ReadRequired(XElement control)
    {
        string? raw = Attr(control, "isrequired");
        return bool.TryParse(raw, out bool required) ? required : null;
    }

    private static IEnumerable<XElement> CellsOf(XElement container) =>
        Descend(container, "rows").SelectMany(r => Children(r, "row")).SelectMany(row => Children(row, "cell"));

    // The base-language (1033) label, falling back to the first label authored.
    private static string? LabelOf(XElement element)
    {
        XElement? labels = Children(element, "labels").FirstOrDefault();
        if (labels == null)
        {
            return NullIfEmpty(Attr(element, "description"));
        }

        List<XElement> all = Children(labels, "label").ToList();
        XElement? chosen = all.FirstOrDefault(l => Attr(l, "languagecode") == BaseLanguageCode.ToString()) ?? all.FirstOrDefault();
        return chosen == null ? null : NullIfEmpty(Attr(chosen, "description"));
    }

    private static bool EqualsClassId(string? value, string expected) =>
        string.Equals(StripBraces(value), StripBraces(expected), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeGuid(string? value)
    {
        string? stripped = StripBraces(value);
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped!.ToLowerInvariant();
    }

    private static string? StripBraces(string? value) => value?.Trim().Trim('{', '}');

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // --- namespace-agnostic XML helpers -----------------------------------------------------------------------

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    // The single well-known container child (e.g. <tabs>, <rows>, <sections>) - tolerant of it being absent.
    private static IEnumerable<XElement> Descend(XElement parent, string localName) => Children(parent, localName);

    private static string? Attr(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? ChildValue(XElement parent, string localName) =>
        Children(parent, localName).FirstOrDefault()?.Value?.Trim();
}
