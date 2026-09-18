namespace Vela.Steps;

/// <summary>
/// Specifies the deployment types where the plugin can execute.
/// </summary>
public enum SupportedDeployment
{
    /// <summary>Plugin only executes on the server.</summary>
    ServerOnly = 0,

    /// <summary>Plugin only executes on offline clients.</summary>
    OfflineOnly = 1,

    /// <summary>Plugin executes both on server and offline clients.</summary>
    Both = 2
}
