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

/// <summary>
/// Mode selector for a step that may only be asynchronous. Unlike <see cref="IModeSelector{TNext}"/> it exposes
/// no <c>Sync()</c> path (the <c>.Sync()</c>/<c>.Async()</c> extensions target <see cref="IModeSelector{TNext}"/>,
/// not this type), so a synchronous registration does not compile. Returned by the generated
/// <c>On&lt;Message&gt;()</c> extension of a Custom API whose <c>allowedcustomprocessingsteptype</c> is
/// <c>AsyncOnly</c> (Phase 3c). <c>Async()</c> is a first-class method here, not an extension.
/// </summary>
/// <typeparam name="TNext">The interface reached after choosing the (asynchronous) mode.</typeparam>
public interface IAsyncOnlyModeSelector<out TNext>
{
    /// <summary>Register this step in asynchronous mode (the only mode this Custom API allows).</summary>
    TNext Async();
}
