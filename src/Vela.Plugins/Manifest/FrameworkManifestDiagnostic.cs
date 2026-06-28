namespace Vela.Manifest;

/// <summary>
/// Describes a discovery diagnostic produced while scanning an assembly.
/// </summary>
public sealed class FrameworkManifestDiagnostic
{
    /// <summary>
    /// Gets or sets the severity.
    /// </summary>
    public FrameworkManifestDiagnosticSeverity Severity { get; set; }

    /// <summary>
    /// Gets or sets the type name associated with the diagnostic.
    /// </summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the diagnostic message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Diagnostic severity.
/// </summary>
public enum FrameworkManifestDiagnosticSeverity
{
    /// <summary>
    /// Informational.
    /// </summary>
    Info = 0,

    /// <summary>
    /// Warning.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// Error.
    /// </summary>
    Error = 2
}
