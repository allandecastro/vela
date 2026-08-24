using System;
using System.Collections.Generic;
using System.Xml.Schema;

namespace Vela.Cli;

/// <summary>
/// Validates FormXML against the official Microsoft <c>FormXml.xsd</c> (Dynamics 365 CE 9.0), embedded in this
/// assembly. The schema has no target namespace, so exported (namespace-less) formxml validates directly. Its
/// <c>xs:include</c> chain (RibbonCore -> RibbonTypes / RibbonWSS) is resolved from the embedded copies. The
/// compiled schema set is built once and cached.
///
/// Note: this is the 9.0 schema. Live modern formxml can carry attributes newer than it declares (e.g.
/// <c>headerdensity</c>, <c>showinformselector</c>, <c>showcommandbar</c>), so strict validation may report those
/// as errors. This is a best-effort aid for authored fixtures, not a gate: <see cref="FormXmlParser"/> is
/// deliberately tolerant and the generation path never rejects a form on schema validity.
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
