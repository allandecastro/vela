using System;
using Vela.Constants;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

// Typed message entry point (On<TRequest>()). Like the permissive OnMessage(...) chain, Vela does not model the
// image rules of an arbitrary request, so only the universal stage rules apply at compile time and image validity
// is enforced at runtime (the Build() backstop). Unlike the permissive chain, the handler is pre-typed to TRequest
// - the request type is stated once, on On<TRequest>(), and the handler's ctx.Request is IMessagePre/PostContext
// <TRequest> without restating it. Filtering is not offered (permissive). The chain is implemented by the generic
// TypedMessageStep<TEntity, TRequest> adapter, which delegates the stage/mode/handler mutations to the underlying
// StepBuilder<TEntity> and, on Handler(...), hands back the builder's real image/settings terminal.

/// <summary>
/// Stage selection for a typed custom / non-modelled message (<c>On&lt;TRequest&gt;()</c>). Same universal stage
/// rules as <see cref="IPermissiveStepStage{TEntity}"/>, but the terminal handler is pre-typed to
/// <typeparamref name="TRequest"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
/// <typeparam name="TRequest">The request type derived from the <c>On&lt;TRequest&gt;()</c> call.</typeparam>
public interface ITypedMessageStepStage<TEntity, TRequest>
    where TEntity : Entity
    where TRequest : OrganizationRequest, new()
{
    /// <summary>Register in the PreValidation stage (outside the transaction, before security checks).</summary>
    IModeSelector<ITypedMessageNoImageStep<TEntity, TRequest>> PreValidation();

    /// <summary>Register in the PreOperation stage (inside the transaction, before the operation).</summary>
    IModeSelector<ITypedMessagePreImageStep<TEntity, TRequest>> PreOperation();

    /// <summary>Register in the PostOperation stage (inside the transaction, after the operation).</summary>
    IModeSelector<ITypedMessagePreAndPostStep<TEntity, TRequest>> PostOperation();
}

/// <summary>Typed-message step at a stage with no image (PreValidation).</summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
/// <typeparam name="TRequest">The request type stated on <c>On&lt;TRequest&gt;()</c>.</typeparam>
public interface ITypedMessageNoImageStep<TEntity, TRequest>
    where TEntity : Entity
    where TRequest : OrganizationRequest, new()
{
    /// <summary>Bind a pre-stage handler, e.g. <c>.Handler(OnX)</c> where <c>void OnX(IMessagePreContext&lt;TRequest&gt; ctx)</c>. <c>ctx.Request</c> is <typeparamref name="TRequest"/> over the live <c>InputParameters</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IMessagePreContext&lt;TRequest&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IMessagePreContext<TRequest>> handler);
}

/// <summary>Typed-message step at a stage that supports a pre-image (PreOperation).</summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
/// <typeparam name="TRequest">The request type stated on <c>On&lt;TRequest&gt;()</c>.</typeparam>
public interface ITypedMessagePreImageStep<TEntity, TRequest>
    where TEntity : Entity
    where TRequest : OrganizationRequest, new()
{
    /// <summary>Bind a pre-stage handler, e.g. <c>.Handler(OnX)</c> where <c>void OnX(IMessagePreContext&lt;TRequest&gt; ctx)</c>. <c>ctx.Request</c> is <typeparamref name="TRequest"/> over the live <c>InputParameters</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IMessagePreContext&lt;TRequest&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler(Action<IMessagePreContext<TRequest>> handler);
}

/// <summary>Typed-message step at a stage that supports a pre-image and a post-image (PostOperation).</summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
/// <typeparam name="TRequest">The request type stated on <c>On&lt;TRequest&gt;()</c>.</typeparam>
public interface ITypedMessagePreAndPostStep<TEntity, TRequest>
    where TEntity : Entity
    where TRequest : OrganizationRequest, new()
{
    /// <summary>Bind a post-stage handler, e.g. <c>.Handler(OnX)</c> where <c>void OnX(IMessagePostContext&lt;TRequest&gt; ctx)</c>. <c>ctx.Request</c> is <typeparamref name="TRequest"/> and <c>ctx.Response</c> a base <see cref="OrganizationResponse"/>, both over the live parameters.</summary>
    /// <param name="handler">Instance method with signature <c>(IMessagePostContext&lt;TRequest&gt;)</c>.</param>
    IPreAndPostImageStep<TEntity> Handler(Action<IMessagePostContext<TRequest>> handler);
}

/// <summary>
/// Stage selection for a typed message that may only be registered asynchronously (a Custom API whose
/// <c>allowedcustomprocessingsteptype</c> is <c>AsyncOnly</c>, Phase 3c). Identical to
/// <see cref="ITypedMessageStepStage{TEntity, TRequest}"/> except each stage returns an
/// <see cref="IAsyncOnlyModeSelector{TNext}"/>, so only <c>.Async()</c> compiles.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
/// <typeparam name="TRequest">The request type stated on the generated <c>On&lt;Message&gt;()</c> call.</typeparam>
public interface IAsyncOnlyTypedMessageStepStage<TEntity, TRequest>
    where TEntity : Entity
    where TRequest : OrganizationRequest, new()
{
    /// <summary>Register in the PreValidation stage (outside the transaction, before security checks).</summary>
    IAsyncOnlyModeSelector<ITypedMessageNoImageStep<TEntity, TRequest>> PreValidation();

    /// <summary>Register in the PreOperation stage (inside the transaction, before the operation).</summary>
    IAsyncOnlyModeSelector<ITypedMessagePreImageStep<TEntity, TRequest>> PreOperation();

    /// <summary>Register in the PostOperation stage (inside the transaction, after the operation).</summary>
    IAsyncOnlyModeSelector<ITypedMessagePreAndPostStep<TEntity, TRequest>> PostOperation();
}

/// <summary>
/// Generic adapter that carries <typeparamref name="TRequest"/> through the typed-message chain while the mutable
/// step state lives on the shared <see cref="StepBuilder{TEntity}"/>. Stage/mode set on the builder; on
/// <c>Handler(...)</c> the builder's real image/settings terminal is returned so the fluent chain continues.
/// The same adapter serves both the permissive <c>On&lt;TRequest&gt;()</c> chain and the async-only chain
/// (<see cref="IAsyncOnlyTypedMessageStepStage{TEntity, TRequest}"/>); which stage interface the caller holds
/// decides whether <c>.Sync()</c> is reachable.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
/// <typeparam name="TRequest">The request type stated on <c>On&lt;TRequest&gt;()</c>.</typeparam>
internal sealed class TypedMessageStep<TEntity, TRequest> :
    ITypedMessageStepStage<TEntity, TRequest>,
    IAsyncOnlyTypedMessageStepStage<TEntity, TRequest>,
    IModeSelector<ITypedMessageNoImageStep<TEntity, TRequest>>,
    IModeSelector<ITypedMessagePreImageStep<TEntity, TRequest>>,
    IModeSelector<ITypedMessagePreAndPostStep<TEntity, TRequest>>,
    IAsyncOnlyModeSelector<ITypedMessageNoImageStep<TEntity, TRequest>>,
    IAsyncOnlyModeSelector<ITypedMessagePreImageStep<TEntity, TRequest>>,
    IAsyncOnlyModeSelector<ITypedMessagePreAndPostStep<TEntity, TRequest>>,
    ITypedMessageNoImageStep<TEntity, TRequest>,
    ITypedMessagePreImageStep<TEntity, TRequest>,
    ITypedMessagePreAndPostStep<TEntity, TRequest>
    where TEntity : Entity
    where TRequest : OrganizationRequest, new()
{
    private readonly StepBuilder<TEntity> _builder;

    public TypedMessageStep(StepBuilder<TEntity> builder) => _builder = builder;

    IModeSelector<ITypedMessageNoImageStep<TEntity, TRequest>> ITypedMessageStepStage<TEntity, TRequest>.PreValidation()
    { _builder.SetStageInternal(PluginStages.PreValidation); return this; }
    IModeSelector<ITypedMessagePreImageStep<TEntity, TRequest>> ITypedMessageStepStage<TEntity, TRequest>.PreOperation()
    { _builder.SetStageInternal(PluginStages.PreOperation); return this; }
    IModeSelector<ITypedMessagePreAndPostStep<TEntity, TRequest>> ITypedMessageStepStage<TEntity, TRequest>.PostOperation()
    { _builder.SetStageInternal(PluginStages.PostOperation); return this; }

    IAsyncOnlyModeSelector<ITypedMessageNoImageStep<TEntity, TRequest>> IAsyncOnlyTypedMessageStepStage<TEntity, TRequest>.PreValidation()
    { _builder.SetStageInternal(PluginStages.PreValidation); return this; }
    IAsyncOnlyModeSelector<ITypedMessagePreImageStep<TEntity, TRequest>> IAsyncOnlyTypedMessageStepStage<TEntity, TRequest>.PreOperation()
    { _builder.SetStageInternal(PluginStages.PreOperation); return this; }
    IAsyncOnlyModeSelector<ITypedMessagePreAndPostStep<TEntity, TRequest>> IAsyncOnlyTypedMessageStepStage<TEntity, TRequest>.PostOperation()
    { _builder.SetStageInternal(PluginStages.PostOperation); return this; }

    ITypedMessageNoImageStep<TEntity, TRequest> IModeSelector<ITypedMessageNoImageStep<TEntity, TRequest>>.Mode(ExecutionMode mode)
    { _builder.SetModeInternal(mode); return this; }
    ITypedMessagePreImageStep<TEntity, TRequest> IModeSelector<ITypedMessagePreImageStep<TEntity, TRequest>>.Mode(ExecutionMode mode)
    { _builder.SetModeInternal(mode); return this; }
    ITypedMessagePreAndPostStep<TEntity, TRequest> IModeSelector<ITypedMessagePreAndPostStep<TEntity, TRequest>>.Mode(ExecutionMode mode)
    { _builder.SetModeInternal(mode); return this; }

    ITypedMessageNoImageStep<TEntity, TRequest> IAsyncOnlyModeSelector<ITypedMessageNoImageStep<TEntity, TRequest>>.Async()
    { _builder.SetModeInternal(ExecutionMode.Asynchronous); return this; }
    ITypedMessagePreImageStep<TEntity, TRequest> IAsyncOnlyModeSelector<ITypedMessagePreImageStep<TEntity, TRequest>>.Async()
    { _builder.SetModeInternal(ExecutionMode.Asynchronous); return this; }
    ITypedMessagePreAndPostStep<TEntity, TRequest> IAsyncOnlyModeSelector<ITypedMessagePreAndPostStep<TEntity, TRequest>>.Async()
    { _builder.SetModeInternal(ExecutionMode.Asynchronous); return this; }

    INoImageStep<TEntity> ITypedMessageNoImageStep<TEntity, TRequest>.Handler(Action<IMessagePreContext<TRequest>> handler)
    { _builder.SetHandlerInternal(handler); return _builder; }
    IPreImageStep<TEntity> ITypedMessagePreImageStep<TEntity, TRequest>.Handler(Action<IMessagePreContext<TRequest>> handler)
    { _builder.SetHandlerInternal(handler); return _builder; }
    IPreAndPostImageStep<TEntity> ITypedMessagePreAndPostStep<TEntity, TRequest>.Handler(Action<IMessagePostContext<TRequest>> handler)
    { _builder.SetHandlerInternal(handler); return _builder; }
}
