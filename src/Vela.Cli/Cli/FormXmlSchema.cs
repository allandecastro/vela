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
/// Validates FormXML against the official Microsoft <c>FormXml.xsd</c> (Dynamics 365 CE 9.0), embedded in this
/// assembly. The schema has no target namespace, so exported (namespace-less) formxml validates directly. Its
/// <c>xs:include</c> chain (RibbonCore -> RibbonTypes / RibbonWSS) is resolved from the embedded copies via a custom
/// resolver. The compiled schema set is built once and cached.
/// </summary>
public static class FormXmlSchema
{
    private const string RootSchema = "FormXml.xsd";

    private static readonly Lazy<XmlSchemaSet> Schemas = new Lazy<XmlSchemaSet>(Build);

    /// <summary>
    /// Validates <paramref name="formXml"/> against FormXml.xsd. Returns the validation errors (empty when valid);
    /// a malformed document surfaces as a single parse error. Never throws for invalid content.
    /// </summary>
    public static IReadOnlyList<string> Validate(string formXml)
    {
        if (string.IsNullOrWhiteSpace(formXml))
        {
            return new[] { "FormXML is empty." };
        }

        List<string> errors = new List<string>();
        XDocument document;
        try
        {
            document = XDocument.Parse(formXml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return new[] { $"Malformed XML: {ex.Message}" };
        }

        document.Validate(Schemas.Value, (_, e) => errors.Add(FormatError(e)));
        return errors;
    }

    /// <summary>True when <paramref name="formXml"/> is schema-valid FormXML.</summary>
    public static bool IsValid(string formXml) => Validate(formXml).Count == 0;

    private static string FormatError(ValidationEventArgs e)
    {
        IXmlLineInfo? line = e.Exception as IXmlLineInfo;
        string position = line != null && line.HasLineInfo() ? $" (line {line.LineNumber}, position {line.LinePosition})" : string.Empty;
        return $"{e.Severity}: {e.Message}{position}";
    }

    private static XmlSchemaSet Build()
    {
        XmlSchemaSet set = new XmlSchemaSet { XmlResolver = new EmbeddedSchemaResolver() };
        using Stream root = OpenSchema(RootSchema);
        using XmlReader reader = XmlReader.Create(root);
        set.Add(null, reader);
        set.Compile();
        return set;
    }

    private static Stream OpenSchema(string fileName)
    {
        Assembly assembly = typeof(FormXmlSchema).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Schemas.{fileName}", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded schema '{fileName}' was not found.");
        return assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded schema '{fileName}' could not be opened.");
    }

    // Resolves the schema's xs:include references (e.g. "RibbonCore.xsd") to the embedded copies, ignoring any base
    // URI since every schema in the chain ships as a flat resource keyed by file name.
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
