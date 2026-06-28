namespace Vela.Manifest;

/// <summary>
/// Scanner behavior options.
/// </summary>
public sealed class FrameworkManifestScanOptions
{
    /// <summary>
    /// Gets or sets whether scanner warnings should fail the scan.
    /// </summary>
    public bool Strict { get; set; }
}
