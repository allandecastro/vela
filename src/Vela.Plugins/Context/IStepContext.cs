using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    /// <summary>
    /// A plugin context typed to a specific message: the full <see cref="ILocalPluginContext"/> surface plus
    /// the message's strongly-typed <see cref="Request"/> and <see cref="Response"/>. The per-message context
    /// interfaces (<c>ICreateContext&lt;TEntity&gt;</c>, <c>IMergeContext&lt;TEntity&gt;</c>, …) specialize this,
    /// so a handler's <c>ctx.Request</c> is inferred - no manual type argument, and a wrong-message handler does
    /// not compile.
    /// </summary>
    /// <typeparam name="TRequest">The SDK request type for the message (e.g. <c>MergeRequest</c>).</typeparam>
    /// <typeparam name="TResponse">The SDK response type for the message (e.g. <c>CreateResponse</c>).</typeparam>
    public interface IStepContext<out TRequest, out TResponse> : ILocalPluginContext
        where TRequest : OrganizationRequest
        where TResponse : OrganizationResponse
    {
        /// <summary>The current message as a typed request over the live <c>InputParameters</c>.</summary>
        TRequest Request { get; }

        /// <summary>
        /// The current message as a typed response over the live <c>OutputParameters</c>. Only populated in the
        /// PostOperation stage (the platform fills <c>OutputParameters</c> after the main operation).
        /// </summary>
        TResponse Response { get; }
    }
}
