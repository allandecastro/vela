using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vela.Context;
using Vela.Constants;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Messages;

namespace Vela.Steps;

/// <summary>
/// Mutable step registration state plus the private mutation helpers behind the fluent builder. The fluent
/// surface itself (the <c>Vela.Steps</c> interfaces: trigger, per-message stages, modes, config, plus the
/// permissive and typed paths) is implemented in the generated partial <c>Steps/StepBuilder.Impl.cs</c>; each
/// transition just calls one of the helpers here and returns the walking builder. <see cref="Build"/> is the
/// runtime backstop the framework calls to materialise the <see cref="StepDefinition"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public sealed partial class StepBuilder<TEntity>
    where TEntity : Entity
{
    private readonly StepDefinition _definition = new();
    private readonly List<ImageDefinition> _preImages = new();
    private readonly List<ImageDefinition> _postImages = new();
    private ImageDefinition? _currentImage;
    private bool _isPreImage;
    private SetOnceModifier _setOnce;
    private string? _requiredEntity;

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
        Relationship = 256,
        SkipDepth = 512,
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

        // A global message has no per-entity message filter: Dataverse registers its steps against primary entity
        // "none" (Associate/Disassociate, whose Target can be either end of the relationship and whose handler
        // discriminates on ctx.Request.Relationship, and the other PluginMessages.GlobalMessages). The ctor stamped
        // TEntity's logical name, so clear it here - otherwise apply fails resolving a non-existent per-entity filter.
        if (PluginMessages.IsGlobalMessage(_definition.Message))
        {
            _definition.EntityLogicalName = string.Empty;
        }

        // A typed trigger whose message Dataverse accepts on one table only (OnSendEmail() on email, OnWinQuote() on
        // quote) cannot be registered on another one. Apply would at best fail on the missing sdkmessagefilter; for
        // an sdkmessage shared by several requests (Win, Close, Cancel) it would register on the sibling table and
        // hand the handler the wrong typed request.
        if (_requiredEntity != null &&
            !string.Equals(_definition.EntityLogicalName, _requiredEntity, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"On{_definition.RequestName}() registers on the '{_definition.Message}' message of table '{_requiredEntity}', " +
                $"but this plugin's table is '{_definition.EntityLogicalName}'. Register the step from an EntityPluginBase<T> " +
                $"whose T is the '{_requiredEntity}' table.");
        }

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
                "filtering attribute) rather than a request-typed On<CreateRequest>() / OnMessage<T>().");
        }

        // Image backstop: the typed chain already prevents an invalid image at compile time, but a step
        // assembled out of order (reflection) or via a request-typed On<TRequest>() / OnMessage<T>() on a
        // modelled message is still validated here.
        foreach (ImageDefinition image in _preImages)
        {
            EnsureImageAllowed(isPreImage: true, image);
        }
        foreach (ImageDefinition image in _postImages)
        {
            EnsureImageAllowed(isPreImage: false, image);
        }

        EnsureNoDuplicates(_definition.FilteringAttributes, "filtering attribute");
        EnsureNoDuplicates(_definition.Relationships, "relationship");
        foreach (ImageDefinition img in _preImages.Concat(_postImages))
        {
            EnsureNoDuplicates(img.Attributes, $"attribute in image '{img.Alias}'");
        }
        // Aliases are unique across every image of the step, whatever record each one targets: Dataverse accepts a
        // Merge step with the same alias on a Target and a SubordinateId image, then delivers only one of them
        // (verified live), so the duplicate is rejected here.
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

        // The key is persisted in a one-line "[vela:stepkey=...]" marker in the step description, so it cannot hold a
        // line break, another control character or the marker's closing ']'.
        string key = stepKey.Trim();
        if (key.IndexOf(']') >= 0 || key.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Step key '{key}' cannot contain a line break, a control character or ']': it is stored in a one-line [vela:stepkey=...] marker.",
                nameof(stepKey));
        }

        MarkSetOnce(SetOnceModifier.StepKey, "WithStepKey");
        _definition.StepKey = key;
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

        // The permissive Handle(Delegate) overload accepts any delegate; validate the shape here so a wrong-arity or
        // non-void handler fails at registration with a clear message instead of an opaque IndexOutOfRange /
        // TargetParameterCount crash mid-dispatch. Every real step handler takes exactly one context parameter.
        System.Reflection.ParameterInfo[] parameters = handler.Method.GetParameters();
        if (parameters.Length != 1 || handler.Method.ReturnType != typeof(void))
        {
            throw new ArgumentException(
                $"Step handler '{handler.Method.Name}' must be a void method taking exactly one context parameter; found {parameters.Length} parameter(s) returning '{handler.Method.ReturnType.Name}'.",
                nameof(handler));
        }

        RuntimeHandler = handler;
        SetHandler(handler.Method.Name);
    }

    // Every trigger records its message here: the request name it was declared with is kept for the typed context,
    // and the step registers on the real sdkmessage (OnSendEmail() -> Send, OnWinQuote() -> Win). A typed trigger
    // whose message Dataverse accepts on a single table also passes that table, checked by Build().
    private void SetMessage(string requestName, string? requiredEntity = null)
    {
        _definition.RequestName = requestName;
        _definition.Message = SdkMessageNames.ToSdkMessage(requestName);
        _requiredEntity = requiredEntity;
    }

    // A trigger stated by type (On<TRequest>(), OnMessage<TMarker>()): the request name comes from the type, and
    // a [SdkMessage] on it (emitted by `vela models --messages`) names the sdkmessage when the table does not.
    // On<TRequest>() / OnAsyncOnly<TRequest>() / OnMessage<TMessage>() pass their name as trigger: they are for Custom
    // APIs and messages without a typed On{Message}() trigger, so a request that has one is rejected (one way per
    // message), naming the trigger to use.
    private void SetMessageFrom<TMessage>(string? trigger = null) where TMessage : class
    {
        string requestName = ExpressionHelper.GetMessageName<TMessage>();
        if (trigger != null && TypedTriggers.Find(requestName) is string typed)
        {
            throw new InvalidOperationException(
                $"{requestName} has a typed trigger: register the step with {typed}() instead of {trigger}<{typeof(TMessage).Name}>(). " +
                $"{trigger}<TRequest>() is only for messages without one.");
        }

        SetMessage(requestName);
        SdkMessageAttribute? declared = typeof(TMessage).GetCustomAttribute<SdkMessageAttribute>(inherit: true);
        if (declared != null)
        {
            _definition.Message = declared.MessageName;
        }
    }

    // Mutation hooks for the generic typed-message adapter (Vela.Steps.TypedStepAdapter<TEntity, TRequest>),
    // which cannot see the private _definition / helpers directly. Same effect as the explicit-interface
    // stage/mode/handler transitions of the guided chain, just reached from the adapter.
    internal void SetStageInternal(PluginStages stage) => _definition.Stage = stage;
    internal void SetModeInternal(ExecutionMode mode) => _definition.Mode = mode;
    internal void SetAsyncModeInternal(bool deleteJobOnSuccess) => SetAsyncMode(deleteJobOnSuccess);
    internal void SetHandlerInternal(Delegate handler) => SetHandler(handler);

    // Setting mutations centralised so the interface implementations stay in lockstep (each explicit
    // implementation is just `Set...(value); return this;`).
    private void SetExecutionOrder(int order)
    {
        MarkSetOnce(SetOnceModifier.ExecutionOrder, "WithExecutionOrder");
        _definition.ExecutionOrder = order;
    }

    private void SetAsyncMode(bool deleteJobOnSuccess)
    {
        _definition.Mode = ExecutionMode.Asynchronous;
        _definition.AsyncAutoDelete = deleteJobOnSuccess;
    }

    private void SetSkipWhenDepthAbove(int maxDepth)
    {
        MarkSetOnce(SetOnceModifier.SkipDepth, "SkipWhenDepthAbove");
        if (maxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "SkipWhenDepthAbove requires a depth of at least 1.");
        }

        _definition.SkipWhenDepthAbove = maxDepth;
    }

    private void SetRelationships(string schemaName, string[] additionalSchemaNames)
    {
        MarkSetOnce(SetOnceModifier.Relationship, "ForRelationship");
        List<string> names = new() { schemaName };
        if (additionalSchemaNames != null)
        {
            names.AddRange(additionalSchemaNames);
        }

        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("ForRelationship requires non-empty relationship schema names.", nameof(schemaName));
            }
        }

        _definition.Relationships = names.Select(n => n.Trim()).ToArray();
    }

    private void SetDescription(string description)
    {
        MarkSetOnce(SetOnceModifier.Description, "WithDescription");
        _definition.Description = description ?? string.Empty;
    }

    private void SetImpersonationUser(System.Guid systemUserId)
    {
        MarkSetOnce(SetOnceModifier.Impersonation, "WithImpersonation");
        if (systemUserId == System.Guid.Empty)
        {
            throw new System.ArgumentException("WithImpersonation requires a non-empty systemuser id.", nameof(systemUserId));
        }

        _definition.ImpersonatingUserId = systemUserId;
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

    private void SetSecureConfigName(string name)
    {
        MarkSetOnce(SetOnceModifier.SecureConfig, "WithSecureConfigFrom");
        // The name only identifies the secret: its value is supplied to `vela apply` (--secure-config NAME=... or
        // the VELA_SECURE_CONFIG_NAME environment variable) and never compiled into the assembly. Restricting it to
        // an identifier keeps it usable as an environment variable suffix.
        if (string.IsNullOrEmpty(name) || !IsSecureConfigName(name))
        {
            throw new ArgumentException(
                "WithSecureConfigFrom takes the NAME of a secure configuration (letters, digits and '_', not starting with a digit), not its value. Pass the value to 'vela apply --secure-config NAME=VALUE'.",
                nameof(name));
        }

        _definition.SecureConfigName = name;
    }

    internal static bool IsSecureConfigName(string name)
    {
        if (char.IsDigit(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    private void StartImage(string alias, bool isPre, string messagePropertyName)
    {
        FinalizeCurrentImage();
        _currentImage = new ImageDefinition { Alias = alias, MessagePropertyName = messagePropertyName };
        _isPreImage = isPre;
    }

    private void AddImageNow(string alias, bool isPre, Expression<Func<TEntity, object?>>[] attributes)
        => AddImageNow(alias, isPre, string.Empty, attributes);

    // An image of the subordinate record of a Merge (the record merged away), registered against SubordinateId.
    private void AddSubordinateImageNow(string alias, bool isPre, Expression<Func<TEntity, object?>>[] attributes)
        => AddImageNow(alias, isPre, StepImageRules.SubordinateId, attributes);

    private void AddImageNow(string alias, bool isPre, string messagePropertyName, Expression<Func<TEntity, object?>>[] attributes)
    {
        StartImage(alias, isPre, messagePropertyName);
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

    private void EnsureImageAllowed(bool isPreImage, ImageDefinition image)
    {
        string message = _definition.Message ?? string.Empty;
        PluginStages stage = _definition.Stage;
        string? reason = StepImageRules.GetRejectionReason(message, (int)stage, isPreImage)
            ?? StepImageRules.GetMessagePropertyRejectionReason(message, image.MessagePropertyName);
        if (reason is null)
        {
            return;
        }

        string imageType = isPreImage ? "PreImage" : "PostImage";
        throw new InvalidOperationException(
            $"{imageType} '{image.Alias}' is not supported for message '{message}' at stage '{stage}': {reason}.");
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
}
