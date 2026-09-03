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
    IReferenceStepStage<TEntity>,
    IPermissiveStepStage<TEntity>,
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
    public ICreateStepStage<TEntity> OnCreateMultiple() { _definition.Message = PluginMessages.CreateMultiple; return this; }
    public IUpdateStepStage<TEntity> OnUpdateMultiple() { _definition.Message = PluginMessages.UpdateMultiple; return this; }
    public IDeleteStepStage<TEntity> OnDelete() { _definition.Message = PluginMessages.Delete; return this; }
    public IReferenceStepStage<TEntity> OnRetrieve() { _definition.Message = PluginMessages.Retrieve; return this; }
    public IReferenceStepStage<TEntity> OnRetrieveMultiple() { _definition.Message = PluginMessages.RetrieveMultiple; return this; }
    public IReferenceStepStage<TEntity> OnAssociate() { _definition.Message = PluginMessages.Associate; return this; }
    public IReferenceStepStage<TEntity> OnDisassociate() { _definition.Message = PluginMessages.Disassociate; return this; }

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

    IModeSelector<IFilteringSelector<TEntity, INoImageStep<TEntity>>> ICreateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, INoImageStep<TEntity>>> ICreateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IPostImageStep<TEntity>>> ICreateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<IFilteringSelector<TEntity, INoImageStep<TEntity>>> IUpdateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IPreImageStep<TEntity>>> IUpdateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>>> IUpdateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<INoImageStep<TEntity>> IDeleteStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IPreImageStep<TEntity>> IDeleteStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IPreImageStep<TEntity>> IDeleteStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<INoImageStep<TEntity>> IReferenceStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<INoImageStep<TEntity>> IReferenceStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<INoImageStep<TEntity>> IReferenceStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IModeSelector<INoImageStep<TEntity>> IPermissiveStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IPreImageStep<TEntity>> IPermissiveStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IPreAndPostImageStep<TEntity>> IPermissiveStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

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

    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.ExecutionOrder(int order) { _definition.ExecutionOrder = order; return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Description(string description) { _definition.Description = description ?? string.Empty; return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Handler(string methodName) { SetHandler(methodName); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Handler(Action<ILocalPluginContext, TEntity> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { _definition.ImpersonationSource = source; return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { _definition.SupportedDeployment = deployment; return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Disabled() { _definition.Enabled = false; return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.UnsecureConfig(string configuration) { _definition.UnsecureConfig = configuration ?? string.Empty; return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.SecureConfig(string configuration) { _definition.SecureConfig = configuration ?? string.Empty; return this; }

    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.ExecutionOrder(int order) { _definition.ExecutionOrder = order; return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Description(string description) { _definition.Description = description ?? string.Empty; return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Handler(string methodName) { SetHandler(methodName); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Handler(Action<ILocalPluginContext, TEntity> handler) { SetHandler(handler); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { _definition.ImpersonationSource = source; return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { _definition.SupportedDeployment = deployment; return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Disabled() { _definition.Enabled = false; return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.UnsecureConfig(string configuration) { _definition.UnsecureConfig = configuration ?? string.Empty; return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.SecureConfig(string configuration) { _definition.SecureConfig = configuration ?? string.Empty; return this; }

    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.ExecutionOrder(int order) { _definition.ExecutionOrder = order; return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Description(string description) { _definition.Description = description ?? string.Empty; return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Handler(string methodName) { SetHandler(methodName); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Handler(Action<ILocalPluginContext, TEntity> handler) { SetHandler(handler); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { _definition.ImpersonationSource = source; return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { _definition.SupportedDeployment = deployment; return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Disabled() { _definition.Enabled = false; return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.UnsecureConfig(string configuration) { _definition.UnsecureConfig = configuration ?? string.Empty; return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.SecureConfig(string configuration) { _definition.SecureConfig = configuration ?? string.Empty; return this; }

    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.ExecutionOrder(int order) { _definition.ExecutionOrder = order; return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Description(string description) { _definition.Description = description ?? string.Empty; return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Handler(string methodName) { SetHandler(methodName); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Handler(Action<ILocalPluginContext, TEntity> handler) { SetHandler(handler); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { _definition.ImpersonationSource = source; return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { _definition.SupportedDeployment = deployment; return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Disabled() { _definition.Enabled = false; return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.UnsecureConfig(string configuration) { _definition.UnsecureConfig = configuration ?? string.Empty; return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.SecureConfig(string configuration) { _definition.SecureConfig = configuration ?? string.Empty; return this; }

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
                "FilteringAttributes(...). An unfiltered Create/Update step fires on every change.");
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

    private void SetHandler(Action<ILocalPluginContext, TEntity> handler)
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

        bool isCreate = string.Equals(message, PluginMessages.Create, StringComparison.OrdinalIgnoreCase);
        bool isUpdate = string.Equals(message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase);
        bool isDelete = string.Equals(message, PluginMessages.Delete, StringComparison.OrdinalIgnoreCase);

        // Permissive: a message Vela does not model (custom messages, custom APIs, or platform messages such
        // as Merge/Route/Send that Vela does not expose as typed methods). Trust the developer; the platform
        // rejects a genuinely invalid image at deploy time.
        if (!isCreate && !isUpdate && !isDelete)
        {
            return true;
        }

        if (stage == PluginStages.PreValidation || stage == PluginStages.MainOperation)
        {
            return false;
        }

        if (stage == PluginStages.PreOperation)
        {
            if (!isPreImage)
            {
                return false;
            }

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
