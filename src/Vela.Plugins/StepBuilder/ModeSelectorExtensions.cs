using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Readability shortcuts for <see cref="IModeSelector{TNext}.Mode(ExecutionMode)"/>: <c>.Sync()</c> and
/// <c>.Async()</c> instead of <c>.Mode(ExecutionMode.Synchronous)</c> / <c>.Mode(ExecutionMode.Asynchronous)</c>.
/// A single generic extension covers every step in the chain (the selector is covariant on its next step).
/// </summary>
public static class ModeSelectorExtensions
{
    /// <summary>Register this step in synchronous mode (equivalent to <c>Mode(ExecutionMode.Synchronous)</c>).</summary>
    /// <typeparam name="TNext">The interface reached after choosing the mode.</typeparam>
    /// <param name="selector">The mode selector.</param>
    public static TNext Sync<TNext>(this IModeSelector<TNext> selector) => selector.Mode(ExecutionMode.Synchronous);

    /// <summary>Register this step in asynchronous mode (equivalent to <c>Mode(ExecutionMode.Asynchronous)</c>).</summary>
    /// <typeparam name="TNext">The interface reached after choosing the mode.</typeparam>
    /// <param name="selector">The mode selector.</param>
    public static TNext Async<TNext>(this IModeSelector<TNext> selector) => selector.Mode(ExecutionMode.Asynchronous);
}
