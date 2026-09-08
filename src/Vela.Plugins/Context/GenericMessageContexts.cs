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

    // Entity-typed variants add an early-bound ctx.Target (the plugin's TEntity over the live 'Target'
    // InputParameter) on top of the request-typed generic context, mirroring the curated
    // IEntityTargetContext<TEntity> pattern. Bound with the Handler<TRequest, TEntity>(...) permissive overload.

    /// <summary>Pre-stage typed context for a custom message step with an early-bound <c>ctx.Target</c>.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    /// <typeparam name="TEntity">The early-bound entity type over the live <c>Target</c> InputParameter.</typeparam>
    public interface IMessagePreContext<out TRequest, TEntity> : IMessagePreContext<TRequest>, IEntityTargetContext<TEntity>
        where TRequest : OrganizationRequest
        where TEntity : Entity
    {
        /// <summary>The registered pre-image as the early-bound entity, or null when none is registered.</summary>
        TEntity? PreImage { get; }
    }

    /// <summary>Post-stage typed context for a custom message step with an early-bound <c>ctx.Target</c>.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    /// <typeparam name="TEntity">The early-bound entity type over the live <c>Target</c> InputParameter.</typeparam>
    public interface IMessagePostContext<out TRequest, TEntity> : IMessagePostContext<TRequest>, IEntityTargetContext<TEntity>
        where TRequest : OrganizationRequest
        where TEntity : Entity
    {
        /// <summary>The registered pre-image as the early-bound entity, or null when none is registered.</summary>
        TEntity? PreImage { get; }
        /// <summary>The registered post-image as the early-bound entity, or null when none is registered.</summary>
        TEntity? PostImage { get; }
    }

    /// <summary>Runtime pre-stage context for a custom message step with an early-bound <c>ctx.Target</c>.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    /// <typeparam name="TEntity">The early-bound entity type over the live <c>Target</c> InputParameter.</typeparam>
    public sealed class MessagePreContext<TRequest, TEntity> : PreStepContext<TRequest>, IMessagePreContext<TRequest, TEntity>
        where TRequest : OrganizationRequest, new()
        where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MessagePreContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity Target => this.GetTarget<TEntity>();
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
    }

    /// <summary>Runtime post-stage context for a custom message step with an early-bound <c>ctx.Target</c>.</summary>
    /// <typeparam name="TRequest">The request type over the live <c>InputParameters</c>.</typeparam>
    /// <typeparam name="TEntity">The early-bound entity type over the live <c>Target</c> InputParameter.</typeparam>
    public sealed class MessagePostContext<TRequest, TEntity> : PostStepContext<TRequest, OrganizationResponse>, IMessagePostContext<TRequest, TEntity>
        where TRequest : OrganizationRequest, new()
        where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MessagePostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity Target => this.GetTarget<TEntity>();
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
        /// <inheritdoc/>
        public TEntity? PostImage => this.GetPostImage<TEntity>();
    }
}
