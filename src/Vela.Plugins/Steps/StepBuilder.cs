using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vela.Context;
using Vela.Constants;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.Steps;

/// <summary>
/// Mutable step registration state plus the private mutation helpers behind the fluent builder. The fluent
/// surface itself (the <c>Vela.Steps</c> interfaces: trigger, per-message stages, modes, config, plus the
/// permissive and typed paths) is implemented in the generated partial <c>Steps/StepBuilder.Impl.cs</c>; each
/// transition just calls one of the helpers here and returns the walking builder. <see cref="Build"/> is the
/// runtime backstop the framework calls to materialise the <see cref="StepDefinition"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
/// <summary>
/// Internal seam letting the typed-image <c>Handle&lt;TImg&gt;</c> extensions retain a wrapper delegate on either
/// the guided builder (<see cref="StepBuilder{TEntity}"/>) or the typed adapter through a single cast.
/// </summary>
internal interface IRuntimeHandlerHost
{
    void SetRuntimeHandler(Delegate runtimeHandler, string manifestMethodName);
}

public sealed partial class StepBuilder<TEntity> : IRuntimeHandlerHost
    where TEntity : Entity
{
    private readonly StepDefinition _definition = new();
    private readonly List<ImageDefinition> _preImages = new();
    private readonly List<ImageDefinition> _postImages = new();
    private ImageDefinition? _currentImage;
    private bool _isPreImage;
    private SetOnceModifier _setOnce;

    // The single-value modifiers on the fluent chain: each is a "last wins" overwrite if called twice, which
    // silently drops the earlier value. They may be set at most once per step; a second call throws (and the
    // VELA003 analyzer flags it in the editor / at build). FilterOn is included because a second FilterOn(...)
    // replaces the earlier attributes rather than adding to them.
    [Flags]
    private enum SetOnceModifier
    {
        None = 0,
        Filtering = 1,
        ExecutionOrder = 2,
        Description = 4,
        StepKey = 8,
        Impersonation = 16,
        SupportedDeployment = 32,
        UnsecureConfig = 64,
        SecureConfig = 128,
    }

    private void MarkSetOnce(SetOnceModifier modifier, string method)
    {
        if ((_setOnce & modifier) != 0)
        {
            throw new InvalidOperationException(
                $"{method}(...) is already set on this step; call it at most once.");
        }

        _setOnce |= modifier;
    }

    /// <summary>
    /// The actual handler delegate for this step, retained so the framework can invoke it directly instead of
    /// re-resolving the handler by method name via reflection. This is what makes lambda handlers (including
    /// ones that capture locals) and the typed-image <c>Handle&lt;TImg&gt;</c> wrapper work at execution time.
    /// The metadata <see cref="StepDefinition"/> stays pure (name only); the delegate lives here on the builder.
    /// </summary>
    internal Delegate? RuntimeHandler { get; private set; }

    /// <summary>
    /// Creates a new step builder for the specified entity type.
    /// </summary>
    public StepBuilder()
    {
        // Get entity logical name from EntityLogicalNameAttribute
        var attribute = typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>();
        _definition.EntityLogicalName = attribute?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant();
    }

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
        // RegisterStep() pattern registers by side-effect and discards it, so the framework calls Build()).
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
                "FilterOn(...). An unfiltered Create/Update step fires on every change. Use the typed " +
                "OnCreate() / OnUpdate() / OnCreateMultiple() / OnUpdateMultiple() methods (which require the " +
                "filtering attribute) rather than the permissive OnMessage(\"...\").");
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

        MarkSetOnce(SetOnceModifier.Filtering, "FilterOn");

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

        MarkSetOnce(SetOnceModifier.StepKey, "WithStepKey");
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

        RuntimeHandler = handler;
        SetHandler(handler.Method.Name);
    }

    // Mutation hooks for the generic typed-message adapter (Vela.Steps.TypedStepAdapter<TEntity, TRequest>),
    // which cannot see the private _definition / helpers directly. Same effect as the explicit-interface
    // stage/mode/handler transitions of the guided chain, just reached from the adapter.
    internal void SetStageInternal(PluginStages stage) => _definition.Stage = stage;
    internal void SetModeInternal(ExecutionMode mode) => _definition.Mode = mode;
    internal void SetHandlerInternal(Delegate handler) => SetHandler(handler);

    // Retains a runtime handler delegate the framework invokes directly, while recording a separate method name
    // for the registration manifest. Used by the typed-image Handle<TImg>(...) extension terminals: their real
    // runtime delegate is a wrapper (it materializes the projection then calls the user handler), distinct from
    // the user handler whose name belongs in the manifest.
    internal void SetRuntimeHandlerInternal(Delegate runtimeHandler, string manifestMethodName)
    {
        RuntimeHandler = runtimeHandler ?? throw new ArgumentNullException(nameof(runtimeHandler));
        SetHandler(manifestMethodName);
    }

    void IRuntimeHandlerHost.SetRuntimeHandler(Delegate runtimeHandler, string manifestMethodName)
        => SetRuntimeHandlerInternal(runtimeHandler, manifestMethodName);

    // Setting mutations centralised so the interface implementations stay in lockstep (each explicit
    // implementation is just `Set...(value); return this;`).
    private void SetExecutionOrder(int order)
    {
        MarkSetOnce(SetOnceModifier.ExecutionOrder, "WithExecutionOrder");
        _definition.ExecutionOrder = order;
    }

    private void SetDescription(string description)
    {
        MarkSetOnce(SetOnceModifier.Description, "WithDescription");
        _definition.Description = description ?? string.Empty;
    }

    private void SetImpersonation(ImpersonationSource source)
    {
        MarkSetOnce(SetOnceModifier.Impersonation, "WithImpersonation");
        _definition.ImpersonationSource = source;
    }

    private void SetSupportedDeployment(SupportedDeployment deployment)
    {
        MarkSetOnce(SetOnceModifier.SupportedDeployment, "WithSupportedDeployment");
        _definition.SupportedDeployment = deployment;
    }

    // Disabled() is a flag toggle: calling it twice is a harmless no-op, so it is intentionally not set-once.
    private void SetDisabled() => _definition.Enabled = false;

    private void SetUnsecureConfig(string configuration)
    {
        MarkSetOnce(SetOnceModifier.UnsecureConfig, "WithUnsecureConfig");
        _definition.UnsecureConfig = configuration ?? string.Empty;
    }

    private void SetSecureConfig(string configuration)
    {
        MarkSetOnce(SetOnceModifier.SecureConfig, "WithSecureConfig");
        _definition.SecureConfig = configuration ?? string.Empty;
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
            throw new InvalidOperationException("No image is being configured. Call WithPreImage() or WithPostImage() first.");
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
