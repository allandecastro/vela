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
/// Fluent builder for configuring plugin step registration.
/// Implements all step configuration interfaces using the chained builder pattern.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public sealed class StepBuilder<TEntity> :
    IStepBuilder<TEntity>,
    IStepMessageBuilder<TEntity>,
    IStepStageBuilder<TEntity>,
    IStepModeBuilder<TEntity>,
    IStepImageBuilder<TEntity>,
    IFilteringStepMessageBuilder<TEntity>,
    IFilteringStepStageBuilder<TEntity>,
    IFilteringStepModeBuilder<TEntity>
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

    /// <summary>
    /// Gets the built step definition.
    /// </summary>
    public StepDefinition Build()
    {
        // Finalize any pending image
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

        EnsureNoDuplicates(_definition.FilteringAttributes, "filtering attribute");
        foreach (ImageDefinition image in _preImages.Concat(_postImages))
        {
            EnsureNoDuplicates(image.Attributes, $"attribute in image '{image.Alias}'");
        }
        EnsureNoDuplicates(_preImages.Concat(_postImages).Select(i => i.Alias).ToArray(), "image alias");

        return _definition;
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

    public IFilteringStepMessageBuilder<TEntity> OnCreate()
    {
        _definition.Message = PluginMessages.Create;
        return this;
    }

    public IFilteringStepMessageBuilder<TEntity> OnUpdate()
    {
        _definition.Message = PluginMessages.Update;
        return this;
    }

    public IFilteringStepMessageBuilder<TEntity> OnCreateMultiple()
    {
        _definition.Message = PluginMessages.CreateMultiple;
        return this;
    }

    public IFilteringStepMessageBuilder<TEntity> OnUpdateMultiple()
    {
        _definition.Message = PluginMessages.UpdateMultiple;
        return this;
    }

    public IStepMessageBuilder<TEntity> OnDelete()
    {
        _definition.Message = PluginMessages.Delete;
        return this;
    }

    public IStepMessageBuilder<TEntity> OnRetrieve()
    {
        _definition.Message = PluginMessages.Retrieve;
        return this;
    }

    public IStepMessageBuilder<TEntity> OnRetrieveMultiple()
    {
        _definition.Message = PluginMessages.RetrieveMultiple;
        return this;
    }

    public IStepMessageBuilder<TEntity> OnAssociate()
    {
        _definition.Message = PluginMessages.Associate;
        return this;
    }

    public IStepMessageBuilder<TEntity> OnDisassociate()
    {
        _definition.Message = PluginMessages.Disassociate;
        return this;
    }

    public IStepMessageBuilder<TEntity> OnSetState()
    {
        _definition.Message = PluginMessages.SetState;
        return this;
    }

    public IStepMessageBuilder<TEntity> OnSetStateDynamicEntity()
    {
        _definition.Message = PluginMessages.SetStateDynamicEntity;
        return this;
    }

    public IStepMessageBuilder<TEntity> OnMessage(string messageName)
    {
        if (string.IsNullOrWhiteSpace(messageName))
        {
            throw new ArgumentNullException(nameof(messageName));
        }
        _definition.Message = messageName;
        return this;
    }

    /// <inheritdoc />
    public IStepMessageBuilder<TEntity> OnMessage<TMessage>() where TMessage : class
    {
        _definition.Message = ExpressionHelper.GetMessageName<TMessage>();
        return this;
    }

    public IStepStageBuilder<TEntity> Stage(PluginStages stage)
    {
        SetStage(stage);
        return this;
    }

    public IStepModeBuilder<TEntity> Mode(ExecutionMode mode)
    {
        _definition.Mode = mode;
        return this;
    }

    // Filtering chain (Create / Update): Stage and Mode are implemented explicitly so the same builder
    // instance can return the filtering-required interfaces, forcing FilteringAttributes(...) before Build().
    IFilteringStepStageBuilder<TEntity> IFilteringStepMessageBuilder<TEntity>.Stage(PluginStages stage)
    {
        SetStage(stage);
        return this;
    }

    IFilteringStepModeBuilder<TEntity> IFilteringStepStageBuilder<TEntity>.Mode(ExecutionMode mode)
    {
        _definition.Mode = mode;
        return this;
    }

    public IStepModeBuilder<TEntity> FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute,
        params Expression<Func<TEntity, object?>>[] additionalAttributes)
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
        return this;
    }

    private void SetStage(PluginStages stage)
    {
        if (stage == PluginStages.MainOperation)
        {
            throw new InvalidOperationException("MainOperation stage is reserved for Custom API main operation plugins.");
        }

        _definition.Stage = stage;
    }

    public IStepModeBuilder<TEntity> ExecutionOrder(int order)
    {
        _definition.ExecutionOrder = order;
        return this;
    }

    public IStepModeBuilder<TEntity> Key(string stepKey)
    {
        if (string.IsNullOrWhiteSpace(stepKey))
        {
            throw new ArgumentNullException(nameof(stepKey));
        }

        _definition.StepKey = stepKey.Trim();
        return this;
    }

    IStepModeBuilder<TEntity> IStepImageBuilder<TEntity>.ExecutionOrder(int order)
    {
        FinalizeCurrentImage();
        _definition.ExecutionOrder = order;
        return this;
    }

    IStepModeBuilder<TEntity> IStepImageBuilder<TEntity>.Key(string stepKey)
    {
        FinalizeCurrentImage();
        return Key(stepKey);
    }

    public IStepModeBuilder<TEntity> Description(string description)
    {
        _definition.Description = description ?? string.Empty;
        return this;
    }

    public IStepModeBuilder<TEntity> Handler(string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentNullException(nameof(methodName));
        }

        _definition.HandlerMethodName = methodName.Trim();
        return this;
    }

    public IStepModeBuilder<TEntity> Handler(Action<ILocalPluginContext, TEntity> handler)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (handler.Method.IsStatic)
        {
            throw new ArgumentException("Handler delegate must reference an instance method.", nameof(handler));
        }

        return Handler(handler.Method.Name);
    }

    public IStepModeBuilder<TEntity> ImpersonateUsing(ImpersonationSource source)
    {
        _definition.ImpersonationSource = source;
        return this;
    }

    public IStepModeBuilder<TEntity> SupportedDeployment(SupportedDeployment deployment)
    {
        _definition.SupportedDeployment = deployment;
        return this;
    }

    public IStepModeBuilder<TEntity> Disabled()
    {
        _definition.Enabled = false;
        return this;
    }

    public IStepModeBuilder<TEntity> UnsecureConfig(string configuration)
    {
        _definition.UnsecureConfig = configuration ?? string.Empty;
        return this;
    }

    public IStepModeBuilder<TEntity> SecureConfig(string configuration)
    {
        _definition.SecureConfig = configuration ?? string.Empty;
        return this;
    }

    public IStepImageBuilder<TEntity> PreImage()
    {
        return PreImage(PluginImageAliases.PreImage);
    }

    public IStepImageBuilder<TEntity> PreImage(string alias)
    {
        EnsureImageAllowed(isPreImage: true);
        FinalizeCurrentImage();
        _currentImage = new ImageDefinition { Alias = alias };
        _isPreImage = true;
        return this;
    }

    public IStepImageBuilder<TEntity> PostImage()
    {
        return PostImage(PluginImageAliases.PostImage);
    }

    public IStepImageBuilder<TEntity> PostImage(string alias)
    {
        EnsureImageAllowed(isPreImage: false);
        FinalizeCurrentImage();
        _currentImage = new ImageDefinition { Alias = alias };
        _isPreImage = false;
        return this;
    }

    public IStepModeBuilder<TEntity> AddPreImage(params Expression<Func<TEntity, object?>>[] attributes)
    {
        IStepImageBuilder<TEntity> builder = PreImage();
        if (attributes != null && attributes.Length > 0)
        {
            builder = builder.Attributes(attributes);
        }

        return (IStepModeBuilder<TEntity>)builder;
    }

    public IStepModeBuilder<TEntity> AddPostImage(params Expression<Func<TEntity, object?>>[] attributes)
    {
        IStepImageBuilder<TEntity> builder = PostImage();
        if (attributes != null && attributes.Length > 0)
        {
            builder = builder.Attributes(attributes);
        }

        return (IStepModeBuilder<TEntity>)builder;
    }

    public IStepImageBuilder<TEntity> Attributes(params Expression<Func<TEntity, object?>>[] attributes)
    {
        if (_currentImage == null)
        {
            throw new InvalidOperationException("No image is being configured. Call PreImage() or PostImage() first.");
        }
        if (attributes != null && attributes.Length > 0)
        {
            _currentImage.Attributes = ExpressionHelper.GetAttributeNames(attributes);
        }
        return this;
    }

    IStepImageBuilder<TEntity> IStepImageBuilder<TEntity>.PreImage()
    {
        return PreImage(PluginImageAliases.PreImage);
    }

    IStepImageBuilder<TEntity> IStepImageBuilder<TEntity>.PreImage(string alias)
    {
        return PreImage(alias);
    }

    IStepImageBuilder<TEntity> IStepImageBuilder<TEntity>.PostImage()
    {
        return PostImage(PluginImageAliases.PostImage);
    }

    IStepImageBuilder<TEntity> IStepImageBuilder<TEntity>.PostImage(string alias)
    {
        return PostImage(alias);
    }

    IStepModeBuilder<TEntity> IStepImageBuilder<TEntity>.Handler(string methodName)
    {
        FinalizeCurrentImage();
        return Handler(methodName);
    }

    IStepModeBuilder<TEntity> IStepImageBuilder<TEntity>.Handler(Action<ILocalPluginContext, TEntity> handler)
    {
        FinalizeCurrentImage();
        return Handler(handler);
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

    private void EnsureImageAllowed(bool isPreImage)
    {
        string message = _definition.Message ?? string.Empty;
        PluginStages stage = _definition.Stage;
        bool allowed = IsImageAllowed(message, stage, isPreImage);
        if (allowed)
        {
            return;
        }

        string imageType = isPreImage ? "PreImage" : "PostImage";
        throw new InvalidOperationException($"{imageType} is not supported for message '{message}' at stage '{stage}'.");
    }

    // Encodes the Dataverse platform's rules for which entity images a step may capture, which depend
    // on the pipeline stage and message. These are platform constraints (the server rejects invalid
    // combinations), not framework choices:
    //   * PreValidation / MainOperation: no images exist yet, so none are allowed.
    //   * PreOperation: only a pre-image, and only for Update/Delete (Create has no prior state).
    //   * PostOperation: pre-image for Update/Delete (prior state), post-image for Create/Update
    //     (committed state; Delete has no post-image because the row is gone).
    //   * CreateMultiple / UpdateMultiple: no images at all. These bulk messages carry a "Targets"
    //     collection rather than a single "Target", and the platform accepts neither "Target" nor
    //     "Targets" as an image's messagepropertyname, so any image registration is rejected. Plugins
    //     on bulk messages must read the entity collection from the input parameters instead.
    private static bool IsImageAllowed(string message, PluginStages stage, bool isPreImage)
    {
        if (string.Equals(message, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, PluginMessages.UpdateMultiple, StringComparison.OrdinalIgnoreCase))
        {
            return false;
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

            return string.Equals(message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message, PluginMessages.Delete, StringComparison.OrdinalIgnoreCase);
        }

        if (stage == PluginStages.PostOperation)
        {
            if (isPreImage)
            {
                return string.Equals(message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(message, PluginMessages.Delete, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(message, PluginMessages.Create, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
