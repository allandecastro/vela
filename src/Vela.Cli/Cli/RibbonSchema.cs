using System;
using System.Collections.Generic;
using System.Xml.Schema;

namespace Vela.Cli;

/// <summary>
/// Validates ribbon XML (a <c>RibbonDiffXml</c> fragment) against the official Microsoft Ribbon schemas embedded in
/// this assembly. <c>RibbonCore.xsd</c> declares the <c>RibbonDiffXml</c> root and includes <c>RibbonTypes.xsd</c>
/// and <c>RibbonWSS.xsd</c>. The compiled schema set is built once and cached.
/// </summary>
public static class RibbonSchema
{
    private static readonly Lazy<XmlSchemaSet> Schemas = new Lazy<XmlSchemaSet>(() => EmbeddedSchemas.BuildSet("RibbonCore.xsd"));

    /// <summary>Validates ribbon XML against the Ribbon schemas. Returns the errors (empty when valid).</summary>
    public static IReadOnlyList<string> Validate(string ribbonXml) => EmbeddedSchemas.Validate(Schemas.Value, ribbonXml);

    /// <summary>True when <paramref name="ribbonXml"/> is schema-valid ribbon XML.</summary>
    public static bool IsValid(string ribbonXml) => Validate(ribbonXml).Count == 0;
}
