using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    // Generic typed contexts for a custom / non-modelled message driven from OnMessage(...). Unlike the curated
    // per-message contexts, these do not name a specific message: the caller supplies the request type at the
    // handler, so ctx.Request is that type over the live InputParameters, for any OrganizationRequest-derived type
    // (an SDK message request or a Vela-generated Custom API request). A pre-stage handler receives
    // IMessagePreContext (typed ctx.Request only); a post-stage handler receives IMessagePostContext (ctx.Request
    // plus a base OrganizationResponse ctx.Response, since the message is not modelled with a typed response).

    /// <summary>Pre-stage typed context for a custom message step (<c>ctx.Request</c> only).</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    public interface IMessagePreContext<out TRequest> : IStepRequestContext<TRequest>
        where TRequest : OrganizationRequest
    {
    }

    /// <summary>Post-stage typed context for a custom message step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    public interface IMessagePostContext<out TRequest> : IStepRequestContext<TRequest>, IStepResponseContext<OrganizationResponse>
        where TRequest : OrganizationRequest
    {
    }

    /// <summary>Runtime pre-stage context for a custom message step.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    public sealed class MessagePreContext<TRequest> : PreStepContext<TRequest>, IMessagePreContext<TRequest>
        where TRequest : OrganizationRequest, new()
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MessagePreContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime post-stage context for a custom message step.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    public sealed class MessagePostContext<TRequest> : PostStepContext<TRequest, OrganizationResponse>, IMessagePostContext<TRequest>
        where TRequest : OrganizationRequest, new()
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MessagePostContext(ILocalPluginContext inner) : base(inner) { }
    }

    // Entity-typed variants add early-bound ctx.PreImage / ctx.PostImage (the plugin's TEntity) on top of the
    // request-typed generic context. Bound with the Handler<TRequest, TEntity>(...) permissive overload. The
    // primary record is read via ctx.Request.Target, already typed by the request.

    /// <summary>Pre-stage typed context for a custom message step with early-bound typed images.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    /// <typeparam name="TEntity">The early-bound entity type for the step's images.</typeparam>
    public interface IMessagePreContext<out TRequest, TEntity> : IMessagePreContext<TRequest>, IPreImageContext<TEntity>
        where TRequest : OrganizationRequest
        where TEntity : Entity
    {
    }

    /// <summary>Post-stage typed context for a custom message step with early-bound typed images.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    /// <typeparam name="TEntity">The early-bound entity type for the step's images.</typeparam>
    public interface IMessagePostContext<out TRequest, TEntity> : IMessagePostContext<TRequest>, IPreImageContext<TEntity>, IPostImageContext<TEntity>
        where TRequest : OrganizationRequest
        where TEntity : Entity
    {
    }

    /// <summary>Runtime pre-stage context for a custom message step with early-bound typed images.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    /// <typeparam name="TEntity">The early-bound entity type for the step's images.</typeparam>
    public sealed class MessagePreContext<TRequest, TEntity> : PreStepContext<TRequest>, IMessagePreContext<TRequest, TEntity>
        where TRequest : OrganizationRequest, new()
        where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MessagePreContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
    }

    /// <summary>Runtime post-stage context for a custom message step with early-bound typed images.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    /// <typeparam name="TEntity">The early-bound entity type for the step's images.</typeparam>
    public sealed class MessagePostContext<TRequest, TEntity> : PostStepContext<TRequest, OrganizationResponse>, IMessagePostContext<TRequest, TEntity>
        where TRequest : OrganizationRequest, new()
        where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MessagePostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
        /// <inheritdoc/>
        public TEntity? PostImage => this.GetPostImage<TEntity>();
    }
}
