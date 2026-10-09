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

    /// <summary>
    /// Adds the early-bound <see cref="PreImage"/> (a snapshot of the step's primary record taken before the
    /// operation) to a message context. A pre-image is always the step's own table, so it is exposed as the
    /// context's <typeparamref name="TEntity"/> - no type argument needed. Only mixed into the messages and stages
    /// where a pre-image exists (Update, Delete, Merge). It reads the pre-image registered under the default alias; for a
    /// custom alias use <c>ctx.GetPreImage&lt;T&gt;("alias")</c> instead.
    /// </summary>
    /// <typeparam name="TEntity">The early-bound entity type of the step's primary record.</typeparam>
    public interface IPreImageContext<TEntity>
        where TEntity : Entity
    {
        /// <summary>The registered pre-image as the early-bound entity, or null when none is registered.</summary>
        TEntity? PreImage { get; }
    }

    /// <summary>
    /// Adds the early-bound <see cref="PostImage"/> (a snapshot of the step's primary record taken after the
    /// operation) to a message context. A post-image is always the step's own table, so it is exposed as the
    /// context's <typeparamref name="TEntity"/> - no type argument needed. Only mixed into the messages and stages
    /// where a post-image exists (Create, Update, Merge - PostOperation). It reads the post-image registered under the
    /// default alias; for a custom alias use <c>ctx.GetPostImage&lt;T&gt;("alias")</c> instead.
    /// </summary>
    /// <typeparam name="TEntity">The early-bound entity type of the step's primary record.</typeparam>
    public interface IPostImageContext<TEntity>
        where TEntity : Entity
    {
        /// <summary>The registered post-image as the early-bound entity, or null when none is registered.</summary>
        TEntity? PostImage { get; }
    }

    /// <summary>
    /// Adds the early-bound <see cref="SubordinatePreImage"/> to a Merge context: a snapshot of the subordinate record
    /// (the record merged away, <c>ctx.Request.SubordinateId</c>) taken before the merge. It reads the image captured
    /// with <c>WithSubordinatePreImage(...)</c> under the default alias; for a custom alias use
    /// <c>ctx.GetPreImage&lt;T&gt;("alias")</c> instead.
    /// </summary>
    /// <typeparam name="TEntity">The early-bound entity type of the merged table.</typeparam>
    public interface ISubordinatePreImageContext<TEntity>
        where TEntity : Entity
    {
        /// <summary>The subordinate pre-image as the early-bound entity, or null when none is registered.</summary>
        TEntity? SubordinatePreImage { get; }
    }

    /// <summary>
    /// Adds the early-bound <see cref="SubordinatePostImage"/> to a Merge context: a snapshot of the subordinate record
    /// taken after the merge, so deactivated and with its <c>masterid</c> pointing at the master. It reads the image
    /// captured with <c>WithSubordinatePostImage(...)</c> under the default alias; for a custom alias use
    /// <c>ctx.GetPostImage&lt;T&gt;("alias")</c> instead.
    /// </summary>
    /// <typeparam name="TEntity">The early-bound entity type of the merged table.</typeparam>
    public interface ISubordinatePostImageContext<TEntity>
        where TEntity : Entity
    {
        /// <summary>The subordinate post-image as the early-bound entity, or null when none is registered.</summary>
        TEntity? SubordinatePostImage { get; }
    }
}
