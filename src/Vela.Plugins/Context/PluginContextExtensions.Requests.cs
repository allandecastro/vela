using System;
using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    /// <summary>
    /// Typed access to the current message as an SDK request/response. The SDK request/response classes
    /// (<see cref="OrganizationRequest"/> / <see cref="OrganizationResponse"/> subclasses) are thin typed views
    /// over a <see cref="ParameterCollection"/>; in a plugin, <c>InputParameters</c> / <c>OutputParameters</c>
    /// are those collections, so a request/response can be hydrated over them at no copy cost. This gives typed
    /// access to every message parameter (e.g. <c>MergeRequest.SubordinateId</c>) for any SDK message.
    /// </summary>
    public static partial class PluginContextExtensions
    {
        /// <summary>
        /// Views the current message's <c>InputParameters</c> as a typed SDK request. Example:
        /// <c>ctx.GetRequest&lt;MergeRequest&gt;().SubordinateId</c>. The request name is not validated against
        /// the executing message - request the type that matches the step's message.
        /// </summary>
        /// <typeparam name="TRequest">The SDK request type (e.g. <c>MergeRequest</c>, <c>RetrieveRequest</c>).</typeparam>
        /// <param name="context">Plugin context.</param>
        /// <returns>A typed request backed by the live <c>InputParameters</c>.</returns>
        public static TRequest GetRequest<TRequest>(this ILocalPluginContext context)
            where TRequest : OrganizationRequest, new()
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return new TRequest { Parameters = context.PluginExecutionContext.InputParameters };
        }

        /// <summary>
        /// Views the current message's <c>OutputParameters</c> as a typed SDK response. Only populated in the
        /// PostOperation stage (the platform fills <c>OutputParameters</c> after the main operation). Example:
        /// <c>ctx.GetResponse&lt;CreateResponse&gt;().id</c>.
        /// </summary>
        /// <typeparam name="TResponse">The SDK response type (e.g. <c>CreateResponse</c>).</typeparam>
        /// <param name="context">Plugin context.</param>
        /// <returns>A typed response backed by the live <c>OutputParameters</c>.</returns>
        public static TResponse GetResponse<TResponse>(this ILocalPluginContext context)
            where TResponse : OrganizationResponse, new()
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return new TResponse { Results = context.PluginExecutionContext.OutputParameters };
        }

        /// <summary>
        /// Views a base <see cref="OrganizationResponse"/> as a typed response. A custom message executed via
        /// <see cref="Microsoft.Xrm.Sdk.IOrganizationService.Execute(OrganizationRequest)"/> returns a base
        /// <see cref="OrganizationResponse"/> that cannot be cast to the generated derived type, so wrap it:
        /// <c>service.Execute(new XxxRequest { ... }).ToResponse&lt;XxxResponse&gt;()</c>. The typed response reads
        /// the same <c>Results</c> bag, so its typed properties return the real output values.
        /// </summary>
        /// <typeparam name="T">The typed response type (an <see cref="OrganizationResponse"/> subclass).</typeparam>
        /// <param name="response">The base response returned by <c>service.Execute(...)</c>.</param>
        /// <returns>A typed response backed by the same <c>Results</c>.</returns>
        public static T ToResponse<T>(this OrganizationResponse response)
            where T : OrganizationResponse, new()
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            return new T { Results = response.Results };
        }
    }
}
