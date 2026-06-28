using System;

namespace Vela.Plugin
{
    /// <summary>
    /// Represents a known business failure that should return a clean message.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="PluginBusinessException"/> class.
    /// </remarks>
    /// <param name="message">Business message.</param>
    public sealed class PluginBusinessException(string message) : Exception(message)
    {
    }
}
