using System;
using Vela.CustomApi;
using Vela.Steps;

namespace Vela.Manifest;

/// <summary>
/// Represents the discovered framework metadata for an assembly.
/// </summary>
public sealed class FrameworkManifest
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
    /// Gets or sets the discovered plugin steps.
    /// </summary>
    public StepDefinition[] PluginSteps { get; set; } = [];

    /// <summary>
    /// Gets or sets the discovered Custom APIs.
    /// </summary>
    public CustomApiDefinition[] CustomApis { get; set; } = [];

    /// <summary>
    /// Gets or sets any diagnostics captured during discovery.
    /// </summary>
    public FrameworkManifestDiagnostic[] Diagnostics { get; set; } = [];
}
