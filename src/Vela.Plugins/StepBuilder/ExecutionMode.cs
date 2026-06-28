namespace Vela.StepBuilder;

/// <summary>
/// Specifies whether the plugin executes synchronously or asynchronously.
/// </summary>
public enum ExecutionMode
{
    /// <summary>Plugin executes synchronously within the transaction.</summary>
    Synchronous = 0,

    /// <summary>Plugin executes asynchronously via the Async Service.</summary>
    Asynchronous = 1
}
