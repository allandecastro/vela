using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Vela.Cli;

/// <summary>
/// Shared machinery for validating XML against the official Microsoft XSDs embedded in this assembly (FormXml and
/// the Ribbon schemas). Compiles a schema set from a root <c>.xsd</c>, resolving its <c>xs:include</c> chain from
/// the embedded copies, and validates a document against it. Used by <see cref="FormXmlSchema"/> and
/// <see cref="RibbonSchema"/>.
/// </summary>
internal static class EmbeddedSchemas
{
    public static XmlSchemaSet BuildSet(string rootSchemaFileName)
    {
        XmlSchemaSet set = new XmlSchemaSet { XmlResolver = new EmbeddedSchemaResolver() };
        using Stream root = OpenSchema(rootSchemaFileName);
        using XmlReader reader = XmlReader.Create(root);
        set.Add(null, reader);
        set.Compile();
        return set;
    }

    /// <summary>Validates <paramref name="xml"/> against <paramref name="schemas"/>, returning the errors (empty when valid).</summary>
    public static IReadOnlyList<string> Validate(XmlSchemaSet schemas, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new[] { "XML is empty." };
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return new[] { $"Malformed XML: {ex.Message}" };
        }

        List<string> errors = new List<string>();
        document.Validate(schemas, (_, e) => errors.Add(FormatError(e)));
        return errors;
    }

    private static string FormatError(ValidationEventArgs e)
    {
        IXmlLineInfo? line = e.Exception as IXmlLineInfo;
        string position = line != null && line.HasLineInfo() ? $" (line {line.LineNumber}, position {line.LinePosition})" : string.Empty;
        return $"{e.Severity}: {e.Message}{position}";
    }

    internal static Stream OpenSchema(string fileName)
    {
        Assembly assembly = typeof(EmbeddedSchemas).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Schemas.{fileName}", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded schema '{fileName}' was not found.");
        return assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded schema '{fileName}' could not be opened.");
    }

    // Resolves xs:include references (e.g. "RibbonCore.xsd") to the embedded copies, ignoring any base URI since
    // every schema ships as a flat resource keyed by file name.
    private sealed class EmbeddedSchemaResolver : XmlResolver
    {
        public override Uri ResolveUri(Uri? baseUri, string? relativeUri) =>
            new Uri("xsd://schemas/" + Path.GetFileName(relativeUri ?? string.Empty));

        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            if (absoluteUri == null)
            {
                throw new ArgumentNullException(nameof(absoluteUri));
            }

            return OpenSchema(Path.GetFileName(absoluteUri.AbsolutePath));
        }
    }
}
