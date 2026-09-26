using System;
using System.Linq.Expressions;
using Vela.Constants;
using Vela.Context;
using Vela.Steps;
using Microsoft.Xrm.Sdk;

namespace Vela.Steps
{
    /// <summary>
    /// Carries <typeparamref name="TRequest"/> through the typed-message chain (<c>On&lt;TRequest&gt;()</c> /
    /// <c>OnAsyncOnly&lt;TRequest&gt;()</c>) while the mutable step state lives on the shared
    /// <see cref="StepBuilder{TEntity}"/>. Stage / mode / images / settings delegate to the builder's internal
    /// hooks; the terminal <c>Handle(...)</c> binds a handler pre-typed to <typeparamref name="TRequest"/>. The
    /// same adapter serves both the sync-or-async chain and the async-only chain; which stage interface the
    /// caller holds decides whether <c>.Sync()</c> is reachable.
    /// </summary>
    /// <typeparam name="TEntity">The entity type for this step.</typeparam>
    /// <typeparam name="TRequest">The request type stated on <c>On&lt;TRequest&gt;()</c>.</typeparam>
    internal sealed class TypedStepAdapter<TEntity, TRequest> :
        ITypedStages<TEntity, TRequest>,
        IAsyncOnlyTypedStages<TEntity, TRequest>,
        ISyncMode<ITypedPreValidationConfig<TEntity, TRequest>>,
        ISyncMode<ITypedPreOperationConfig<TEntity, TRequest>>,
        ISyncAsyncMode<ITypedPostOperationConfig<TEntity, TRequest>>,
        IAsyncMode<ITypedPostOperationConfig<TEntity, TRequest>>,
        ITypedPreValidationConfig<TEntity, TRequest>,
        ITypedPreOperationConfig<TEntity, TRequest>,
        ITypedPostOperationConfig<TEntity, TRequest>
        where TEntity : Entity
        where TRequest : OrganizationRequest, new()
    {
        private readonly StepBuilder<TEntity> _b;

        public TypedStepAdapter(StepBuilder<TEntity> builder) => _b = builder;

        // ---- Stage choosers ----
        ISyncMode<ITypedPreValidationConfig<TEntity, TRequest>> ITypedStages<TEntity, TRequest>.PreValidation() { _b.SetStageInternal(PluginStages.PreValidation); return this; }
        ISyncMode<ITypedPreOperationConfig<TEntity, TRequest>> ITypedStages<TEntity, TRequest>.PreOperation() { _b.SetStageInternal(PluginStages.PreOperation); return this; }
        ISyncAsyncMode<ITypedPostOperationConfig<TEntity, TRequest>> ITypedStages<TEntity, TRequest>.PostOperation() { _b.SetStageInternal(PluginStages.PostOperation); return this; }

        IAsyncMode<ITypedPostOperationConfig<TEntity, TRequest>> IAsyncOnlyTypedStages<TEntity, TRequest>.PostOperation() { _b.SetStageInternal(PluginStages.PostOperation); return this; }

        // ---- Modes ----
        ITypedPreValidationConfig<TEntity, TRequest> ISyncMode<ITypedPreValidationConfig<TEntity, TRequest>>.Sync() { _b.SetModeInternal(ExecutionMode.Synchronous); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ISyncMode<ITypedPreOperationConfig<TEntity, TRequest>>.Sync() { _b.SetModeInternal(ExecutionMode.Synchronous); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ISyncAsyncMode<ITypedPostOperationConfig<TEntity, TRequest>>.Sync() { _b.SetModeInternal(ExecutionMode.Synchronous); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ISyncAsyncMode<ITypedPostOperationConfig<TEntity, TRequest>>.Async() { _b.SetModeInternal(ExecutionMode.Asynchronous); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> IAsyncMode<ITypedPostOperationConfig<TEntity, TRequest>>.Async() { _b.SetModeInternal(ExecutionMode.Asynchronous); return this; }

        // ---- PreValidation config (no images) ----
        ITypedPreValidationConfig<TEntity, TRequest> ITypedPreValidationConfig<TEntity, TRequest>.WithDescription(string description) { _b.SetDescriptionInternal(description); return this; }
        ITypedPreValidationConfig<TEntity, TRequest> ITypedPreValidationConfig<TEntity, TRequest>.WithExecutionOrder(int order) { _b.SetExecutionOrderInternal(order); return this; }
        ITypedPreValidationConfig<TEntity, TRequest> ITypedPreValidationConfig<TEntity, TRequest>.WithStepKey(string key) { _b.SetStepKeyInternal(key); return this; }
        ITypedPreValidationConfig<TEntity, TRequest> ITypedPreValidationConfig<TEntity, TRequest>.WithImpersonation(ImpersonationSource source) { _b.SetImpersonationInternal(source); return this; }
        ITypedPreValidationConfig<TEntity, TRequest> ITypedPreValidationConfig<TEntity, TRequest>.WithImpersonation(Guid systemUserId) { _b.SetImpersonationUserInternal(systemUserId); return this; }
        ITypedPreValidationConfig<TEntity, TRequest> ITypedPreValidationConfig<TEntity, TRequest>.WithSupportedDeployment(SupportedDeployment deployment) { _b.SetSupportedDeploymentInternal(deployment); return this; }
        ITypedPreValidationConfig<TEntity, TRequest> ITypedPreValidationConfig<TEntity, TRequest>.WithUnsecureConfig(string configuration) { _b.SetUnsecureConfigInternal(configuration); return this; }
        ITypedPreValidationConfig<TEntity, TRequest> ITypedPreValidationConfig<TEntity, TRequest>.WithSecureConfig(string configuration) { _b.SetSecureConfigInternal(configuration); return this; }
        ITypedPreValidationConfig<TEntity, TRequest> ITypedPreValidationConfig<TEntity, TRequest>.Disabled() { _b.SetDisabledInternal(); return this; }
        void ITypedPreValidationConfig<TEntity, TRequest>.Handle(Action<IMessagePreContext<TRequest>> handler) { _b.SetHandlerInternal(handler); }

        // ---- PreOperation config (pre-image) ----
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithDescription(string description) { _b.SetDescriptionInternal(description); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithPreImage(params Expression<Func<TEntity, object?>>[] attributes) { _b.AddImageInternal(PluginImageAliases.PreImage, true, attributes); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithPreImage(string alias, params Expression<Func<TEntity, object?>>[] attributes) { _b.AddImageInternal(alias, true, attributes); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithExecutionOrder(int order) { _b.SetExecutionOrderInternal(order); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithStepKey(string key) { _b.SetStepKeyInternal(key); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithImpersonation(ImpersonationSource source) { _b.SetImpersonationInternal(source); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithImpersonation(Guid systemUserId) { _b.SetImpersonationUserInternal(systemUserId); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithSupportedDeployment(SupportedDeployment deployment) { _b.SetSupportedDeploymentInternal(deployment); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithUnsecureConfig(string configuration) { _b.SetUnsecureConfigInternal(configuration); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.WithSecureConfig(string configuration) { _b.SetSecureConfigInternal(configuration); return this; }
        ITypedPreOperationConfig<TEntity, TRequest> ITypedPreOperationConfig<TEntity, TRequest>.Disabled() { _b.SetDisabledInternal(); return this; }
        void ITypedPreOperationConfig<TEntity, TRequest>.Handle(Action<IMessagePreContext<TRequest>> handler) { _b.SetHandlerInternal(handler); }
        void ITypedPreOperationConfig<TEntity, TRequest>.Handle(Delegate handler) { _b.SetHandlerInternal(handler); }

        // ---- PostOperation config (pre-image + post-image) ----
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithDescription(string description) { _b.SetDescriptionInternal(description); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithPreImage(params Expression<Func<TEntity, object?>>[] attributes) { _b.AddImageInternal(PluginImageAliases.PreImage, true, attributes); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithPreImage(string alias, params Expression<Func<TEntity, object?>>[] attributes) { _b.AddImageInternal(alias, true, attributes); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithPostImage(params Expression<Func<TEntity, object?>>[] attributes) { _b.AddImageInternal(PluginImageAliases.PostImage, false, attributes); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithPostImage(string alias, params Expression<Func<TEntity, object?>>[] attributes) { _b.AddImageInternal(alias, false, attributes); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithExecutionOrder(int order) { _b.SetExecutionOrderInternal(order); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithStepKey(string key) { _b.SetStepKeyInternal(key); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithImpersonation(ImpersonationSource source) { _b.SetImpersonationInternal(source); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithImpersonation(Guid systemUserId) { _b.SetImpersonationUserInternal(systemUserId); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithSupportedDeployment(SupportedDeployment deployment) { _b.SetSupportedDeploymentInternal(deployment); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithUnsecureConfig(string configuration) { _b.SetUnsecureConfigInternal(configuration); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.WithSecureConfig(string configuration) { _b.SetSecureConfigInternal(configuration); return this; }
        ITypedPostOperationConfig<TEntity, TRequest> ITypedPostOperationConfig<TEntity, TRequest>.Disabled() { _b.SetDisabledInternal(); return this; }
        void ITypedPostOperationConfig<TEntity, TRequest>.Handle(Action<IMessagePostContext<TRequest>> handler) { _b.SetHandlerInternal(handler); }
        void ITypedPostOperationConfig<TEntity, TRequest>.Handle(Delegate handler) { _b.SetHandlerInternal(handler); }

        // IFluentStep's GetType/GetHashCode/ToString/Equals are satisfied by the inherited System.Object members.
    }
}
