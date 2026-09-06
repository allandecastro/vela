using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    /// <summary>
    /// A plugin context typed to a specific message that exposes the strongly-typed <see cref="Request"/> (a view
    /// over the live <c>InputParameters</c>). This is the shape available in <b>every</b> stage - the request is
    /// present from PreValidation onwards. The per-message <c>I{Message}PreContext</c> / <c>I{Message}PostContext</c>
    /// interfaces specialize this, so a handler's <c>ctx.Request</c> is inferred with no manual type argument.
    /// </summary>
    /// <typeparam name="TRequest">The SDK request type for the message (e.g. <c>MergeRequest</c>).</typeparam>
    public interface IStepRequestContext<out TRequest> : ILocalPluginContext
        where TRequest : OrganizationRequest
    {
        /// <summary>The current message as a typed request over the live <c>InputParameters</c>.</summary>
        TRequest Request { get; }
    }

    /// <summary>
    /// Adds the strongly-typed <see cref="Response"/> (a view over the live <c>OutputParameters</c>) to a message
    /// context. The platform only fills <c>OutputParameters</c> after the main operation, so this is exposed
    /// <b>only</b> on the PostOperation context (<c>I{Message}PostContext</c>). A PreValidation / PreOperation
    /// handler receives an <c>I{Message}PreContext</c> instead, which has no <c>Response</c> - reading it in a
    /// pre-stage does not compile.
    /// </summary>
    /// <typeparam name="TResponse">The SDK response type for the message (e.g. <c>CreateResponse</c>).</typeparam>
    public interface IStepResponseContext<out TResponse>
        where TResponse : OrganizationResponse
    {
        /// <summary>The current message as a typed response over the live <c>OutputParameters</c> (Post only).</summary>
        TResponse Response { get; }
    }
}
