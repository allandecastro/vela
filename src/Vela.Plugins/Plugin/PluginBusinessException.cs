using System;

namespace Vela.Plugin
{
    /// <summary>
    /// Represents a known business failure that should return a clean message to the caller.
    /// </summary>
    /// <remarks>
    /// This is the sanctioned business-error type: <see cref="Vela.Plugin.PluginBase"/> catches it, records a
    /// dedicated <c>business-exception</c> trace snapshot, and surfaces its message to the caller as a clean
    /// <see cref="Microsoft.Xrm.Sdk.InvalidPluginExecutionException"/>. It cannot derive from
    /// <c>InvalidPluginExecutionException</c> (the SDK seals that type), so the <c>VELA012</c> analyzer
    /// recognizes it by name as a user-facing exception and does not flag it.
    /// </remarks>
    /// <param name="message">Business message.</param>
    public sealed class PluginBusinessException(string message) : Exception(message)
    {
    }
}
