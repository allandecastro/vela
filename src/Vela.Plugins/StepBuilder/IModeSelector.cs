using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Selects the execution mode (synchronous or asynchronous) for a step, then advances to
/// <typeparamref name="TNext"/> - either the capability terminal, or (for Create/Update) the
/// filtering-required step.
/// </summary>
/// <typeparam name="TNext">The interface reached after choosing the mode.</typeparam>
public interface IModeSelector<out TNext>
{
    /// <summary>Configure the execution mode for this step.</summary>
    /// <param name="mode">Synchronous or asynchronous.</param>
    TNext Mode(ExecutionMode mode);
}
