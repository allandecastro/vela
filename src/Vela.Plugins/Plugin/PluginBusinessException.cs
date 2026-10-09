using System;

namespace Vela.Plugin
{
    /// <summary>
    /// Represents a known business failure that should return a clean message to the caller.
    /// </summary>
    /// <remarks>
    /// This is the sanctioned business-error type: <see cref="Vela.Plugin.PluginBase"/> catches it, traces it with its
    /// stack trace, records a dedicated <c>business-exception</c> trace snapshot, and surfaces its message to the
    /// caller as a clean <see cref="Microsoft.Xrm.Sdk.InvalidPluginExecutionException"/>. It cannot derive from
    /// <c>InvalidPluginExecutionException</c> (the SDK seals that type), so the <c>VELA012</c> analyzer
    /// recognizes it by name as a user-facing exception and does not flag it.
    /// </remarks>
    public sealed class PluginBusinessException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginBusinessException"/> class.
        /// </summary>
        /// <param name="message">Business message.</param>
        public PluginBusinessException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginBusinessException"/> class with an error code.
        /// </summary>
        /// <remarks>
        /// The code does not replace the error code the caller sees: Dataverse reports every business failure as
        /// <c>0x80040265</c>. It reaches an SDK caller as the <c>SubErrorCode</c> entry of the fault's
        /// <c>ErrorDetails</c>; the Web API does not return it.
        /// </remarks>
        /// <param name="message">Business message.</param>
        /// <param name="errorCode">Error code, reported as the fault's <c>SubErrorCode</c> detail.</param>
        public PluginBusinessException(string message, int errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginBusinessException"/> class with the exception that
        /// caused it. The inner exception is written to the plug-in trace log; the caller sees only the message.
        /// </summary>
        /// <param name="message">Business message.</param>
        /// <param name="innerException">The exception that caused the business failure.</param>
        public PluginBusinessException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Gets the error code passed to the constructor, or <c>null</c> when none was given.
        /// </summary>
        public int? ErrorCode { get; }
    }
}
