using System;
using System.Collections.Generic;
using System.Xml.Schema;

namespace Vela.Cli;

/// <summary>
/// Validates FormXML against the official Microsoft <c>FormXml.xsd</c> (Dynamics 365 CE 9.0), embedded in this
/// assembly. The schema has no target namespace, so exported (namespace-less) formxml validates directly. Its
/// <c>xs:include</c> chain (RibbonCore -> RibbonTypes / RibbonWSS) is resolved from the embedded copies. The
/// compiled schema set is built once and cached.
/// </summary>
public static class FormXmlSchema
{
    private static readonly Lazy<XmlSchemaSet> Schemas = new Lazy<XmlSchemaSet>(() => EmbeddedSchemas.BuildSet("FormXml.xsd"));

    /// <summary>
    /// Validates <paramref name="formXml"/> against FormXml.xsd. Returns the validation errors (empty when valid);
    /// a malformed document surfaces as a single parse error. Never throws for invalid content.
    /// </summary>
    public static IReadOnlyList<string> Validate(string formXml) => EmbeddedSchemas.Validate(Schemas.Value, formXml);

    /// <summary>True when <paramref name="formXml"/> is schema-valid FormXML.</summary>
    public static bool IsValid(string formXml) => Validate(formXml).Count == 0;
}
