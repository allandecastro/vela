namespace Vela.Steps;

/// <summary>
/// Specifies which user context the plugin impersonates.
/// </summary>
public enum ImpersonationSource
{
    /// <summary>Plugin executes as the user who initiated the request.</summary>
    CallingUser = 0,

    /// <summary>Plugin executes as the owner of the plugin assembly.</summary>
    PluginOwner = 1
}
