using System;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Stage selection for a custom / non-modelled message (<c>OnMessage(...)</c>). Vela cannot know the image
/// rules of an arbitrary message, so only the universal stage rules apply at compile time: no image in
/// PreValidation, a pre-image from PreOperation, and both images in PostOperation. Message-specific validity
/// (for example a platform message that supports no image) is left to the runtime backstop / the platform at
/// deploy time - Vela does not guess.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPermissiveStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage (outside the transaction, before security checks).</summary>
    IModeSelector<IPermissiveNoImageStep<TEntity>> PreValidation();

    /// <summary>Register in the PreOperation stage (inside the transaction, before the operation).</summary>
    IModeSelector<IPermissivePreImageStep<TEntity>> PreOperation();

    /// <summary>Register in the PostOperation stage (inside the transaction, after the operation).</summary>
    IModeSelector<IPermissivePreAndPostStep<TEntity>> PostOperation();
}

// A non-modelled OnMessage(...) step has no typed context, so its handler takes the plain ILocalPluginContext
// (read inputs via ctx.GetRequest<T>() / ctx.GetInputParameter). Each permissive step adds that untyped
// .Handler; settings and image starters are inherited from the shared capability terminal.

/// <summary>Permissive step at a stage with no image (PreValidation).</summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPermissiveNoImageStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind an untyped handler, e.g. <c>.Handler(OnCustom)</c> where <c>void OnCustom(ILocalPluginContext ctx)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ILocalPluginContext)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ILocalPluginContext> handler);

    /// <summary>Bind a typed handler, e.g. <c>.Handler&lt;XxxRequest&gt;(OnCustom)</c> where <c>void OnCustom(IMessagePreContext&lt;XxxRequest&gt; ctx)</c>. <c>ctx.Request</c> is <typeparamref name="TRequest"/> over the live <c>InputParameters</c>.</summary>
    /// <typeparam name="TRequest">Any <see cref="OrganizationRequest"/>-derived type with a parameterless constructor (an SDK request or a generated Custom API request).</typeparam>
    /// <param name="handler">Instance method with signature <c>(IMessagePreContext&lt;TRequest&gt;)</c>.</param>
    INoImageStep<TEntity> Handler<TRequest>(Action<IMessagePreContext<TRequest>> handler) where TRequest : OrganizationRequest, new();

    /// <summary>Bind an entity-typed handler, e.g. <c>.Handler&lt;XxxRequest&gt;(OnCustom)</c> where <c>void OnCustom(IMessagePreContext&lt;XxxRequest, TEntity&gt; ctx)</c>. Adds an early-bound <c>ctx.Target</c> (the step's own <typeparamref name="TEntity"/>) on top of the typed <c>ctx.Request</c>.</summary>
    /// <typeparam name="TRequest">Any <see cref="OrganizationRequest"/>-derived type with a parameterless constructor (an SDK request or a generated Custom API request).</typeparam>
    /// <param name="handler">Instance method with signature <c>(IMessagePreContext&lt;TRequest, TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler<TRequest>(Action<IMessagePreContext<TRequest, TEntity>> handler) where TRequest : OrganizationRequest, new();
}

/// <summary>Permissive step at a stage that supports a pre-image (PreOperation).</summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPermissivePreImageStep<TEntity> : IPreImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind an untyped handler, e.g. <c>.Handler(OnCustom)</c> where <c>void OnCustom(ILocalPluginContext ctx)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ILocalPluginContext)</c>.</param>
    IPreImageStep<TEntity> Handler(Action<ILocalPluginContext> handler);

    /// <summary>Bind a typed handler, e.g. <c>.Handler&lt;XxxRequest&gt;(OnCustom)</c> where <c>void OnCustom(IMessagePreContext&lt;XxxRequest&gt; ctx)</c>. <c>ctx.Request</c> is <typeparamref name="TRequest"/> over the live <c>InputParameters</c>.</summary>
    /// <typeparam name="TRequest">Any <see cref="OrganizationRequest"/>-derived type with a parameterless constructor (an SDK request or a generated Custom API request).</typeparam>
    /// <param name="handler">Instance method with signature <c>(IMessagePreContext&lt;TRequest&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler<TRequest>(Action<IMessagePreContext<TRequest>> handler) where TRequest : OrganizationRequest, new();

    /// <summary>Bind an entity-typed handler, e.g. <c>.Handler&lt;XxxRequest&gt;(OnCustom)</c> where <c>void OnCustom(IMessagePreContext&lt;XxxRequest, TEntity&gt; ctx)</c>. Adds an early-bound <c>ctx.Target</c> (the step's own <typeparamref name="TEntity"/>) on top of the typed <c>ctx.Request</c>.</summary>
    /// <typeparam name="TRequest">Any <see cref="OrganizationRequest"/>-derived type with a parameterless constructor (an SDK request or a generated Custom API request).</typeparam>
    /// <param name="handler">Instance method with signature <c>(IMessagePreContext&lt;TRequest, TEntity&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler<TRequest>(Action<IMessagePreContext<TRequest, TEntity>> handler) where TRequest : OrganizationRequest, new();
}

/// <summary>Permissive step at a stage that supports a pre-image and a post-image (PostOperation).</summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPermissivePreAndPostStep<TEntity> : IPreAndPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind an untyped handler, e.g. <c>.Handler(OnCustom)</c> where <c>void OnCustom(ILocalPluginContext ctx)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ILocalPluginContext)</c>.</param>
    IPreAndPostImageStep<TEntity> Handler(Action<ILocalPluginContext> handler);

    /// <summary>Bind a typed handler, e.g. <c>.Handler&lt;XxxRequest&gt;(OnCustom)</c> where <c>void OnCustom(IMessagePostContext&lt;XxxRequest&gt; ctx)</c>. <c>ctx.Request</c> is <typeparamref name="TRequest"/> and <c>ctx.Response</c> a base <see cref="OrganizationResponse"/>, both over the live parameters.</summary>
    /// <typeparam name="TRequest">Any <see cref="OrganizationRequest"/>-derived type with a parameterless constructor (an SDK request or a generated Custom API request).</typeparam>
    /// <param name="handler">Instance method with signature <c>(IMessagePostContext&lt;TRequest&gt;)</c>.</param>
    IPreAndPostImageStep<TEntity> Handler<TRequest>(Action<IMessagePostContext<TRequest>> handler) where TRequest : OrganizationRequest, new();

    /// <summary>Bind an entity-typed handler, e.g. <c>.Handler&lt;XxxRequest&gt;(OnCustom)</c> where <c>void OnCustom(IMessagePostContext&lt;XxxRequest, TEntity&gt; ctx)</c>. Adds an early-bound <c>ctx.Target</c> (the step's own <typeparamref name="TEntity"/>) on top of the typed <c>ctx.Request</c> / <c>ctx.Response</c>.</summary>
    /// <typeparam name="TRequest">Any <see cref="OrganizationRequest"/>-derived type with a parameterless constructor (an SDK request or a generated Custom API request).</typeparam>
    /// <param name="handler">Instance method with signature <c>(IMessagePostContext&lt;TRequest, TEntity&gt;)</c>.</param>
    IPreAndPostImageStep<TEntity> Handler<TRequest>(Action<IMessagePostContext<TRequest, TEntity>> handler) where TRequest : OrganizationRequest, new();
}
