using System;

namespace Vela.CustomApi;

/// <summary>
/// Represents a manifest of Custom APIs extracted from an assembly.
/// </summary>
public sealed class CustomApiManifest
{
    /// <summary>
    /// Gets or sets the source assembly name.
    /// </summary>
    public string SourceAssemblyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp when the manifest was generated.
    /// </summary>
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the Custom APIs in the manifest.
    /// </summary>
    public CustomApiDefinition[] CustomApis { get; set; } = [];
}
