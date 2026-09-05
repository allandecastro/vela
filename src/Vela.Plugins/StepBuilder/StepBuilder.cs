using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vela.Context;
using Vela.Constants;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.StepBuilder;

/// <summary>
/// Fluent builder for configuring plugin step registration. Implements the whole typed step chain
/// (message -> stage -> mode -> filtering -> settings/images); most transition methods are explicit
/// interface implementations returning <c>this</c>, so a single mutable builder walks the type-state.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public sealed class StepBuilder<TEntity> :
    IStepBuilder<TEntity>,
    ICreateStepStage<TEntity>,
    IUpdateStepStage<TEntity>,
    IDeleteStepStage<TEntity>,
    IRetrieveStepStage<TEntity>,
    IRetrieveMultipleStepStage<TEntity>,
    IAssociateStepStage<TEntity>,
    IDisassociateStepStage<TEntity>,
    IGrantAccessStepStage<TEntity>,
    IModifyAccessStepStage<TEntity>,
    IRevokeAccessStepStage<TEntity>,
    IPermissiveStepStage<TEntity>,
    IMergeStepStage<TEntity>,
    IModeSelector<IMergeStep<TEntity>>,
    IMergeStep<TEntity>,
    IModeSelector<IRetrieveStep<TEntity>>, IRetrieveStep<TEntity>,
    IModeSelector<IRetrieveMultipleStep<TEntity>>, IRetrieveMultipleStep<TEntity>,
    IModeSelector<IAssociateStep<TEntity>>, IAssociateStep<TEntity>,
    IModeSelector<IDisassociateStep<TEntity>>, IDisassociateStep<TEntity>,
    IModeSelector<IGrantAccessStep<TEntity>>, IGrantAccessStep<TEntity>,
    IModeSelector<IModifyAccessStep<TEntity>>, IModifyAccessStep<TEntity>,
    IModeSelector<IRevokeAccessStep<TEntity>>, IRevokeAccessStep<TEntity>,
    ICreateMultipleStepStage<TEntity>,
    IUpdateMultipleStepStage<TEntity>,
    IModeSelector<IDeleteNoImageStep<TEntity>>, IDeleteNoImageStep<TEntity>,
    IModeSelector<IDeletePreImageStep<TEntity>>, IDeletePreImageStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, ICreateNoImageStep<TEntity>>>, IFilteringSelector<TEntity, ICreateNoImageStep<TEntity>>, ICreateNoImageStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, ICreatePostImageStep<TEntity>>>, IFilteringSelector<TEntity, ICreatePostImageStep<TEntity>>, ICreatePostImageStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, ICreateMultipleNoImageStep<TEntity>>>, IFilteringSelector<TEntity, ICreateMultipleNoImageStep<TEntity>>, ICreateMultipleNoImageStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePostImageStep<TEntity>>>, IFilteringSelector<TEntity, ICreateMultiplePostImageStep<TEntity>>, ICreateMultiplePostImageStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdateNoImageStep<TEntity>>>, IFilteringSelector<TEntity, IUpdateNoImageStep<TEntity>>, IUpdateNoImageStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreImageStep<TEntity>>>, IFilteringSelector<TEntity, IUpdatePreImageStep<TEntity>>, IUpdatePreImageStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreAndPostStep<TEntity>>>, IFilteringSelector<TEntity, IUpdatePreAndPostStep<TEntity>>, IUpdatePreAndPostStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultipleNoImageStep<TEntity>>>, IFilteringSelector<TEntity, IUpdateMultipleNoImageStep<TEntity>>, IUpdateMultipleNoImageStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreImageStep<TEntity>>>, IFilteringSelector<TEntity, IUpdateMultiplePreImageStep<TEntity>>, IUpdateMultiplePreImageStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreAndPostStep<TEntity>>>, IFilteringSelector<TEntity, IUpdateMultiplePreAndPostStep<TEntity>>, IUpdateMultiplePreAndPostStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, INoImageStep<TEntity>>>,
    IModeSelector<IFilteringSelector<TEntity, IPreImageStep<TEntity>>>,
    IModeSelector<IFilteringSelector<TEntity, IPostImageStep<TEntity>>>,
    IModeSelector<IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>>>,
    IModeSelector<INoImageStep<TEntity>>,
    IModeSelector<IPreImageStep<TEntity>>,
    IModeSelector<IPreAndPostImageStep<TEntity>>,
    IFilteringSelector<TEntity, INoImageStep<TEntity>>,
    IFilteringSelector<TEntity, IPreImageStep<TEntity>>,
    IFilteringSelector<TEntity, IPostImageStep<TEntity>>,
    IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>>,
    INoImageStep<TEntity>,
    IPreImageConfig<TEntity>,
    IPostImageConfig<TEntity>,
    IPreAndPostImageConfig<TEntity>
    where TEntity : Entity
{
    private readonly StepDefinition _definition = new();
    private readonly List<ImageDefinition> _preImages = new();
    private readonly List<ImageDefinition> _postImages = new();
    private ImageDefinition? _currentImage;
    private bool _isPreImage;

    /// <summary>
    /// Creates a new step builder for the specified entity type.
    /// </summary>
    public StepBuilder()
    {
        // Get entity logical name from EntityLogicalNameAttribute
        var attribute = typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>();
        _definition.EntityLogicalName = attribute?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant();
    }

    // ---- Message (entry, IStepBuilder) -------------------------------------------------------------

    public ICreateStepStage<TEntity> OnCreate() { _definition.Message = PluginMessages.Create; return this; }
    public IUpdateStepStage<TEntity> OnUpdate() { _definition.Message = PluginMessages.Update; return this; }
    public ICreateMultipleStepStage<TEntity> OnCreateMultiple() { _definition.Message = PluginMessages.CreateMultiple; return this; }
    public IUpdateMultipleStepStage<TEntity> OnUpdateMultiple() { _definition.Message = PluginMessages.UpdateMultiple; return this; }
    public IDeleteStepStage<TEntity> OnDelete() { _definition.Message = PluginMessages.Delete; return this; }
    public IRetrieveStepStage<TEntity> OnRetrieve() { _definition.Message = PluginMessages.Retrieve; return this; }
    public IRetrieveMultipleStepStage<TEntity> OnRetrieveMultiple() { _definition.Message = PluginMessages.RetrieveMultiple; return this; }
    public IAssociateStepStage<TEntity> OnAssociate() { _definition.Message = PluginMessages.Associate; return this; }
    public IDisassociateStepStage<TEntity> OnDisassociate() { _definition.Message = PluginMessages.Disassociate; return this; }
    public IMergeStepStage<TEntity> OnMerge() { _definition.Message = PluginMessages.Merge; return this; }
    public IGrantAccessStepStage<TEntity> OnGrantAccess() { _definition.Message = PluginMessages.GrantAccess; return this; }
    public IModifyAccessStepStage<TEntity> OnModifyAccess() { _definition.Message = PluginMessages.ModifyAccess; return this; }
    public IRevokeAccessStepStage<TEntity> OnRevokeAccess() { _definition.Message = PluginMessages.RevokeAccess; return this; }

    public IPermissiveStepStage<TEntity> OnMessage(string messageName)
    {
        if (string.IsNullOrWhiteSpace(messageName))
        {
            throw new ArgumentNullException(nameof(messageName));
        }
        _definition.Message = messageName;
        return this;
    }

    public IPermissiveStepStage<TEntity> OnMessage<TMessage>() where TMessage : class
    {
        _definition.Message = ExpressionHelper.GetMessageName<TMessage>();
        return this;
    }

    // ---- Stage selectors ---------------------------------------------------------------------------

    IModeSelector<IFilteringSelector<TEntity, ICreateNoImageStep<TEntity>>> ICreateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, ICreateNoImageStep<TEntity>>> ICreateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, ICreatePostImageStep<TEntity>>> ICreateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IFilteringSelector<TEntity, ICreateMultipleNoImageStep<TEntity>>> ICreateMultipleStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, ICreateMultipleNoImageStep<TEntity>>> ICreateMultipleStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePostImageStep<TEntity>>> ICreateMultipleStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IFilteringSelector<TEntity, IUpdateNoImageStep<TEntity>>> IUpdateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreImageStep<TEntity>>> IUpdateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreAndPostStep<TEntity>>> IUpdateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IFilteringSelector<TEntity, IUpdateMultipleNoImageStep<TEntity>>> IUpdateMultipleStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreImageStep<TEntity>>> IUpdateMultipleStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreAndPostStep<TEntity>>> IUpdateMultipleStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IDeleteNoImageStep<TEntity>> IDeleteStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IDeletePreImageStep<TEntity>> IDeleteStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IDeletePreImageStep<TEntity>> IDeleteStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IRetrieveStep<TEntity>> IRetrieveStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRetrieveStep<TEntity>> IRetrieveStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRetrieveStep<TEntity>> IRetrieveStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IRetrieveMultipleStep<TEntity>> IRetrieveMultipleStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRetrieveMultipleStep<TEntity>> IRetrieveMultipleStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRetrieveMultipleStep<TEntity>> IRetrieveMultipleStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IAssociateStep<TEntity>> IAssociateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IAssociateStep<TEntity>> IAssociateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IAssociateStep<TEntity>> IAssociateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IDisassociateStep<TEntity>> IDisassociateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IDisassociateStep<TEntity>> IDisassociateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IDisassociateStep<TEntity>> IDisassociateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IGrantAccessStep<TEntity>> IGrantAccessStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IGrantAccessStep<TEntity>> IGrantAccessStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IGrantAccessStep<TEntity>> IGrantAccessStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IModifyAccessStep<TEntity>> IModifyAccessStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IModifyAccessStep<TEntity>> IModifyAccessStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IModifyAccessStep<TEntity>> IModifyAccessStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IRevokeAccessStep<TEntity>> IRevokeAccessStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRevokeAccessStep<TEntity>> IRevokeAccessStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRevokeAccessStep<TEntity>> IRevokeAccessStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<INoImageStep<TEntity>> IPermissiveStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IPreImageStep<TEntity>> IPermissiveStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IPreAndPostImageStep<TEntity>> IPermissiveStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IMergeStep<TEntity>> IMergeStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IMergeStep<TEntity>> IMergeStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IMergeStep<TEntity>> IMergeStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IMergeStep<TEntity> IModeSelector<IMergeStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IRetrieveStep<TEntity> IModeSelector<IRetrieveStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IRetrieveMultipleStep<TEntity> IModeSelector<IRetrieveMultipleStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IAssociateStep<TEntity> IModeSelector<IAssociateStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IDisassociateStep<TEntity> IModeSelector<IDisassociateStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IGrantAccessStep<TEntity> IModeSelector<IGrantAccessStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IModifyAccessStep<TEntity> IModeSelector<IModifyAccessStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IRevokeAccessStep<TEntity> IModeSelector<IRevokeAccessStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }

    INoImageStep<TEntity> IMergeStep<TEntity>.Handler(Action<IMergeContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IRetrieveStep<TEntity>.Handler(Action<IRetrieveContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IRetrieveMultipleStep<TEntity>.Handler(Action<IRetrieveMultipleContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IAssociateStep<TEntity>.Handler(Action<IAssociateContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IDisassociateStep<TEntity>.Handler(Action<IDisassociateContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IGrantAccessStep<TEntity>.Handler(Action<IGrantAccessContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IModifyAccessStep<TEntity>.Handler(Action<IModifyAccessContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IRevokeAccessStep<TEntity>.Handler(Action<IRevokeAccessContext<TEntity>> handler) { SetHandler(handler); return this; }

    // ---- Imaged messages: mode selectors -----------------------------------------------------------
    IDeleteNoImageStep<TEntity> IModeSelector<IDeleteNoImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IDeletePreImageStep<TEntity> IModeSelector<IDeletePreImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, ICreateNoImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, ICreateNoImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, ICreatePostImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, ICreatePostImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, ICreateMultipleNoImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, ICreateMultipleNoImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, ICreateMultiplePostImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePostImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IUpdateNoImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdateNoImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IUpdatePreImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdatePreImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IUpdatePreAndPostStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdatePreAndPostStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IUpdateMultipleNoImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdateMultipleNoImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IUpdateMultiplePreImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IUpdateMultiplePreAndPostStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreAndPostStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }

    // ---- Imaged messages: filtering selectors ------------------------------------------------------
    ICreateNoImageStep<TEntity> IFilteringSelector<TEntity, ICreateNoImageStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    ICreatePostImageStep<TEntity> IFilteringSelector<TEntity, ICreatePostImageStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    ICreateMultipleNoImageStep<TEntity> IFilteringSelector<TEntity, ICreateMultipleNoImageStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    ICreateMultiplePostImageStep<TEntity> IFilteringSelector<TEntity, ICreateMultiplePostImageStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IUpdateNoImageStep<TEntity> IFilteringSelector<TEntity, IUpdateNoImageStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IUpdatePreImageStep<TEntity> IFilteringSelector<TEntity, IUpdatePreImageStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IUpdatePreAndPostStep<TEntity> IFilteringSelector<TEntity, IUpdatePreAndPostStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IUpdateMultipleNoImageStep<TEntity> IFilteringSelector<TEntity, IUpdateMultipleNoImageStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IUpdateMultiplePreImageStep<TEntity> IFilteringSelector<TEntity, IUpdateMultiplePreImageStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IUpdateMultiplePreAndPostStep<TEntity> IFilteringSelector<TEntity, IUpdateMultiplePreAndPostStep<TEntity>>.FilteringAttributes(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }

    // ---- Imaged messages: typed handlers -----------------------------------------------------------
    INoImageStep<TEntity> IDeleteNoImageStep<TEntity>.Handler(Action<IDeleteContext<TEntity>> handler) { SetHandler(handler); return this; }
    IPreImageStep<TEntity> IDeletePreImageStep<TEntity>.Handler(Action<IDeleteContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> ICreateNoImageStep<TEntity>.Handler(Action<ICreateContext<TEntity>> handler) { SetHandler(handler); return this; }
    IPostImageStep<TEntity> ICreatePostImageStep<TEntity>.Handler(Action<ICreateContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> ICreateMultipleNoImageStep<TEntity>.Handler(Action<ICreateMultipleContext<TEntity>> handler) { SetHandler(handler); return this; }
    IPostImageStep<TEntity> ICreateMultiplePostImageStep<TEntity>.Handler(Action<ICreateMultipleContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IUpdateNoImageStep<TEntity>.Handler(Action<IUpdateContext<TEntity>> handler) { SetHandler(handler); return this; }
    IPreImageStep<TEntity> IUpdatePreImageStep<TEntity>.Handler(Action<IUpdateContext<TEntity>> handler) { SetHandler(handler); return this; }
    IPreAndPostImageStep<TEntity> IUpdatePreAndPostStep<TEntity>.Handler(Action<IUpdateContext<TEntity>> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IUpdateMultipleNoImageStep<TEntity>.Handler(Action<IUpdateMultipleContext<TEntity>> handler) { SetHandler(handler); return this; }
    IPreImageStep<TEntity> IUpdateMultiplePreImageStep<TEntity>.Handler(Action<IUpdateMultipleContext<TEntity>> handler) { SetHandler(handler); return this; }
    IPreAndPostImageStep<TEntity> IUpdateMultiplePreAndPostStep<TEntity>.Handler(Action<IUpdateMultipleContext<TEntity>> handler) { SetHandler(handler); return this; }

    // ---- Mode selectors ----------------------------------------------------------------------------

    IFilteringSelector<TEntity, INoImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, INoImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IPreImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IPreImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IPostImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IPostImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IModeSelector<INoImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IPreImageStep<TEntity> IModeSelector<IPreImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IPreAndPostImageStep<TEntity> IModeSelector<IPreAndPostImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }

    // ---- Filtering selectors -----------------------------------------------------------------------

    INoImageStep<TEntity> IFilteringSelector<TEntity, INoImageStep<TEntity>>.FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)
    { SetFilteringAttributes(attribute, additionalAttributes); return this; }

    IPreImageStep<TEntity> IFilteringSelector<TEntity, IPreImageStep<TEntity>>.FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)
    { SetFilteringAttributes(attribute, additionalAttributes); return this; }

    IPostImageStep<TEntity> IFilteringSelector<TEntity, IPostImageStep<TEntity>>.FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)
    { SetFilteringAttributes(attribute, additionalAttributes); return this; }

    IPreAndPostImageStep<TEntity> IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>>.FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)
    { SetFilteringAttributes(attribute, additionalAttributes); return this; }

    // ---- Image starters ----------------------------------------------------------------------------

    IPreImageConfig<TEntity> IPreImageStep<TEntity>.PreImage() { StartImage(PluginImageAliases.PreImage, isPre: true); return this; }
    IPreImageConfig<TEntity> IPreImageStep<TEntity>.PreImage(string alias) { StartImage(alias, isPre: true); return this; }
    IPreImageStep<TEntity> IPreImageStep<TEntity>.AddPreImage(params Expression<Func<TEntity, object?>>[] attributes) { AddImageNow(PluginImageAliases.PreImage, isPre: true, attributes); return this; }

    IPostImageConfig<TEntity> IPostImageStep<TEntity>.PostImage() { StartImage(PluginImageAliases.PostImage, isPre: false); return this; }
    IPostImageConfig<TEntity> IPostImageStep<TEntity>.PostImage(string alias) { StartImage(alias, isPre: false); return this; }
    IPostImageStep<TEntity> IPostImageStep<TEntity>.AddPostImage(params Expression<Func<TEntity, object?>>[] attributes) { AddImageNow(PluginImageAliases.PostImage, isPre: false, attributes); return this; }

    IPreAndPostImageConfig<TEntity> IPreAndPostImageStep<TEntity>.PreImage() { StartImage(PluginImageAliases.PreImage, isPre: true); return this; }
    IPreAndPostImageConfig<TEntity> IPreAndPostImageStep<TEntity>.PreImage(string alias) { StartImage(alias, isPre: true); return this; }
    IPreAndPostImageConfig<TEntity> IPreAndPostImageStep<TEntity>.PostImage() { StartImage(PluginImageAliases.PostImage, isPre: false); return this; }
    IPreAndPostImageConfig<TEntity> IPreAndPostImageStep<TEntity>.PostImage(string alias) { StartImage(alias, isPre: false); return this; }
    IPreAndPostImageStep<TEntity> IPreAndPostImageStep<TEntity>.AddPreImage(params Expression<Func<TEntity, object?>>[] attributes) { AddImageNow(PluginImageAliases.PreImage, isPre: true, attributes); return this; }
    IPreAndPostImageStep<TEntity> IPreAndPostImageStep<TEntity>.AddPostImage(params Expression<Func<TEntity, object?>>[] attributes) { AddImageNow(PluginImageAliases.PostImage, isPre: false, attributes); return this; }

    // ---- Image attribute configuration -------------------------------------------------------------

    IPreImageConfig<TEntity> IPreImageConfig<TEntity>.Attributes(params Expression<Func<TEntity, object?>>[] attributes) { SetCurrentImageAttributes(attributes); return this; }
    IPostImageConfig<TEntity> IPostImageConfig<TEntity>.Attributes(params Expression<Func<TEntity, object?>>[] attributes) { SetCurrentImageAttributes(attributes); return this; }
    IPreAndPostImageConfig<TEntity> IPreAndPostImageConfig<TEntity>.Attributes(params Expression<Func<TEntity, object?>>[] attributes) { SetCurrentImageAttributes(attributes); return this; }

    // ---- Settings (IStepConfig, per capability terminal) -------------------------------------------

    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.ExecutionOrder(int order) { SetExecutionOrder(order); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Description(string description) { SetDescription(description); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Handler(string methodName) { SetHandler(methodName); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Handler(Action<ILocalPluginContext, TEntity> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { SetImpersonation(source); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { SetSupportedDeployment(deployment); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Disabled() { SetDisabled(); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.UnsecureConfig(string configuration) { SetUnsecureConfig(configuration); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.SecureConfig(string configuration) { SetSecureConfig(configuration); return this; }

    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.ExecutionOrder(int order) { SetExecutionOrder(order); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Description(string description) { SetDescription(description); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Handler(string methodName) { SetHandler(methodName); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Handler(Action<ILocalPluginContext, TEntity> handler) { SetHandler(handler); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { SetImpersonation(source); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { SetSupportedDeployment(deployment); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Disabled() { SetDisabled(); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.UnsecureConfig(string configuration) { SetUnsecureConfig(configuration); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.SecureConfig(string configuration) { SetSecureConfig(configuration); return this; }

    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.ExecutionOrder(int order) { SetExecutionOrder(order); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Description(string description) { SetDescription(description); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Handler(string methodName) { SetHandler(methodName); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Handler(Action<ILocalPluginContext, TEntity> handler) { SetHandler(handler); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { SetImpersonation(source); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { SetSupportedDeployment(deployment); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Disabled() { SetDisabled(); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.UnsecureConfig(string configuration) { SetUnsecureConfig(configuration); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.SecureConfig(string configuration) { SetSecureConfig(configuration); return this; }

    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.ExecutionOrder(int order) { SetExecutionOrder(order); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Description(string description) { SetDescription(description); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Handler(string methodName) { SetHandler(methodName); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Handler(Action<ILocalPluginContext, TEntity> handler) { SetHandler(handler); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { SetImpersonation(source); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { SetSupportedDeployment(deployment); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Disabled() { SetDisabled(); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.UnsecureConfig(string configuration) { SetUnsecureConfig(configuration); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.SecureConfig(string configuration) { SetSecureConfig(configuration); return this; }

    // ---- Build (shared across all terminals) -------------------------------------------------------

    /// <summary>
    /// Gets the built step definition. Runs the runtime backstop: filtering requirement, duplicate checks,
    /// and image validity against the message/stage (regardless of how the builder was assembled).
    /// </summary>
    public StepDefinition Build()
    {
        FinalizeCurrentImage();

        _definition.PreImages = _preImages.ToArray();
        _definition.PostImages = _postImages.ToArray();

        // Hard guarantee (the type-state only forces filtering when the fluent chain is consumed; the
        // RegisterSteps() pattern registers by side-effect and discards it, so the framework calls Build()).
        // Create/Update must declare at least one filtering attribute — an unfiltered step fires on every change.
        bool requiresFiltering =
            string.Equals(_definition.Message, PluginMessages.Create, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_definition.Message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_definition.Message, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_definition.Message, PluginMessages.UpdateMultiple, StringComparison.OrdinalIgnoreCase);
        if (requiresFiltering && _definition.FilteringAttributes.Length == 0)
        {
            throw new InvalidOperationException(
                $"Step on message '{_definition.Message}' must declare at least one filtering attribute via " +
                "FilteringAttributes(...) after Mode(...). An unfiltered Create/Update step fires on every " +
                "change. Use the typed OnCreate() / OnUpdate() / OnCreateMultiple() / OnUpdateMultiple() " +
                "methods (which require the filtering attribute) rather than the permissive OnMessage(\"...\").");
        }

        // Image backstop: the typed chain already prevents an invalid image at compile time, but a step
        // assembled out of order (reflection) or via the permissive OnMessage(...) escape hatch with a
        // modelled message string is still validated here.
        foreach (ImageDefinition image in _preImages)
        {
            EnsureImageAllowed(isPreImage: true, image.Alias);
        }
        foreach (ImageDefinition image in _postImages)
        {
            EnsureImageAllowed(isPreImage: false, image.Alias);
        }

        EnsureNoDuplicates(_definition.FilteringAttributes, "filtering attribute");
        foreach (ImageDefinition img in _preImages.Concat(_postImages))
        {
            EnsureNoDuplicates(img.Attributes, $"attribute in image '{img.Alias}'");
        }
        EnsureNoDuplicates(_preImages.Concat(_postImages).Select(i => i.Alias).ToArray(), "image alias");

        return _definition;
    }

    // ---- Private mutation helpers ------------------------------------------------------------------

    private void SetFilteringAttributes(
        Expression<Func<TEntity, object?>> attribute,
        Expression<Func<TEntity, object?>>[] additionalAttributes)
    {
        if (attribute == null)
        {
            throw new ArgumentNullException(nameof(attribute));
        }

        List<Expression<Func<TEntity, object?>>> all = new() { attribute };
        if (additionalAttributes != null)
        {
            all.AddRange(additionalAttributes);
        }

        _definition.FilteringAttributes = ExpressionHelper.GetAttributeNames(all.ToArray());
    }

    private void SetKey(string stepKey)
    {
        if (string.IsNullOrWhiteSpace(stepKey))
        {
            throw new ArgumentNullException(nameof(stepKey));
        }

        _definition.StepKey = stepKey.Trim();
    }

    private void SetHandler(string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentNullException(nameof(methodName));
        }

        _definition.HandlerMethodName = methodName.Trim();
    }

    private void SetHandler(Delegate handler)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (handler.Method.IsStatic)
        {
            throw new ArgumentException("Handler delegate must reference an instance method.", nameof(handler));
        }

        SetHandler(handler.Method.Name);
    }

    // Setting mutations centralised so the four per-terminal IStepConfig implementations stay in lockstep
    // (each explicit implementation is just `Set...(value); return this;`).
    private void SetExecutionOrder(int order) => _definition.ExecutionOrder = order;
    private void SetDescription(string description) => _definition.Description = description ?? string.Empty;
    private void SetImpersonation(ImpersonationSource source) => _definition.ImpersonationSource = source;
    private void SetSupportedDeployment(SupportedDeployment deployment) => _definition.SupportedDeployment = deployment;
    private void SetDisabled() => _definition.Enabled = false;
    private void SetUnsecureConfig(string configuration) => _definition.UnsecureConfig = configuration ?? string.Empty;
    private void SetSecureConfig(string configuration) => _definition.SecureConfig = configuration ?? string.Empty;

    private void StartImage(string alias, bool isPre)
    {
        FinalizeCurrentImage();
        _currentImage = new ImageDefinition { Alias = alias };
        _isPreImage = isPre;
    }

    private void AddImageNow(string alias, bool isPre, Expression<Func<TEntity, object?>>[] attributes)
    {
        StartImage(alias, isPre);
        if (attributes != null && attributes.Length > 0)
        {
            SetCurrentImageAttributes(attributes);
        }
        FinalizeCurrentImage();
    }

    private void SetCurrentImageAttributes(Expression<Func<TEntity, object?>>[] attributes)
    {
        if (_currentImage == null)
        {
            throw new InvalidOperationException("No image is being configured. Call PreImage() or PostImage() first.");
        }
        if (attributes != null && attributes.Length > 0)
        {
            _currentImage.Attributes = ExpressionHelper.GetAttributeNames(attributes);
        }
    }

    private void FinalizeCurrentImage()
    {
        if (_currentImage != null)
        {
            if (_isPreImage)
            {
                _preImages.Add(_currentImage);
            }
            else
            {
                _postImages.Add(_currentImage);
            }
            _currentImage = null;
        }
    }

    private void EnsureImageAllowed(bool isPreImage, string alias)
    {
        string message = _definition.Message ?? string.Empty;
        PluginStages stage = _definition.Stage;
        if (IsImageAllowed(message, stage, isPreImage))
        {
            return;
        }

        string imageType = isPreImage ? "PreImage" : "PostImage";
        throw new InvalidOperationException(
            $"{imageType} '{alias}' is not supported for message '{message}' at stage '{stage}'.");
    }

    private static void EnsureNoDuplicates(string[]? names, string what)
    {
        if (names == null || names.Length == 0)
        {
            return;
        }

        string? duplicate = names
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .FirstOrDefault();

        if (duplicate != null)
        {
            throw new InvalidOperationException($"Duplicate {what}: '{duplicate}' is specified more than once.");
        }
    }

    // Encodes the stage/message rules for which entity images a step may capture. This is the runtime
    // backstop behind the typed builder (the type-state already blocks an invalid image at compile time for
    // the modelled messages). Only the modelled messages (Create/Update/Delete, and the bulk variants
    // evaluated as their single-row counterparts) are enforced; any other message is permissive - Vela does
    // not model its image rules, so the platform arbitrates at deploy time.
    //   * PreValidation / MainOperation: no images exist yet, so none are allowed.
    //   * PreOperation: only a pre-image, and only for Update/Delete (Create has no prior state).
    //   * PostOperation: pre-image for Update/Delete (prior state), post-image for Create/Update
    //     (committed state; Delete has no post-image because the row is gone).
    private static bool IsImageAllowed(string message, PluginStages stage, bool isPreImage)
    {
        // Bulk messages capture per-target image collections under the same stage/message rules as their
        // single-row counterparts, so evaluate them as Create/Update.
        if (string.Equals(message, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase))
        {
            message = PluginMessages.Create;
        }
        else if (string.Equals(message, PluginMessages.UpdateMultiple, StringComparison.OrdinalIgnoreCase))
        {
            message = PluginMessages.Update;
        }

        // Universal rules (platform-wide, independent of the message): no image exists before or outside the
        // operation, and a post-image never exists before the write is committed. These bind even for a
        // non-modelled message, so they are checked before the permissive short-circuit below.
        if (stage == PluginStages.PreValidation || stage == PluginStages.MainOperation)
        {
            return false;
        }
        if (stage == PluginStages.PreOperation && !isPreImage)
        {
            return false;
        }

        bool isCreate = string.Equals(message, PluginMessages.Create, StringComparison.OrdinalIgnoreCase);
        bool isUpdate = string.Equals(message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase);
        bool isDelete = string.Equals(message, PluginMessages.Delete, StringComparison.OrdinalIgnoreCase);

        // Permissive: a message Vela does not model (custom messages, custom APIs, or platform messages such
        // as Merge/Route/Send that Vela does not expose as typed methods). The universal rules above still
        // applied; the message-specific part is trusted here and the platform rejects a genuinely invalid
        // image at deploy time.
        if (!isCreate && !isUpdate && !isDelete)
        {
            return true;
        }

        if (stage == PluginStages.PreOperation)
        {
            // isPreImage is guaranteed true here (the universal rule rejected a pre-operation post-image).
            return isUpdate || isDelete;
        }

        if (stage == PluginStages.PostOperation)
        {
            if (isPreImage)
            {
                return isUpdate || isDelete;
            }

            return isCreate || isUpdate;
        }

        return false;
    }
}
