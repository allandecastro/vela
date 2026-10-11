namespace Vela.StepApiGen;

/// <summary>A config-interface member that returns the config itself (fluent, order-free).</summary>
/// <param name="Signature">Name and parameter list, e.g. <c>WithStepKey(string key)</c>.</param>
/// <param name="Body">The StepBuilder statement that applies it, e.g. <c>SetKey(key);</c>.</param>
/// <param name="Doc">XML doc lines emitted above the interface member (without the <c>///</c>), or null for none.</param>
internal sealed record Modifier(string Signature, string Body, IReadOnlyList<string>? Doc = null);

/// <summary>A terminal <c>Handle(...)</c> overload of a config interface.</summary>
/// <param name="Parameter">The handler parameter, e.g. <c>Action&lt;ICreatePreContext&lt;TEntity&gt;&gt; handler</c>.</param>
/// <param name="TypeParameters">Method type parameters (e.g. <c>&lt;TRequest&gt;</c>), or empty.</param>
/// <param name="Constraints">Method constraints as written on the interface (with a leading space), or empty.</param>
internal sealed record HandleOverload(string Parameter, string TypeParameters = "", string Constraints = "");

/// <summary>
/// The shape of one stage config interface (<c>I{Message}{Stage}Config&lt;TEntity&gt;</c> and friends): which
/// optional modifiers it offers and its typed Handle overloads. Shared by the API and impl emitters so the
/// interface and its explicit implementation can never drift apart.
/// </summary>
internal sealed record StepConfig(
    string Type,
    string Constraints,
    string Summary,
    Stage Stage,
    bool FilterOn,
    bool PreImage,
    bool PostImage,
    IReadOnlyList<HandleOverload> Handles,
    bool SubordinatePreImage = false,
    bool SubordinatePostImage = false,
    bool Relationship = false)
{
    public const string Attributes = "params Expression<Func<TEntity, object?>>[] attributes";

    /// <summary>
    /// Doc of a PreValidation <c>WithPreImage</c>: a top-level PreValidation step runs outside the transaction, so
    /// its image can be stale by the main operation. Only PreValidation configs carry it.
    /// </summary>
    private static readonly IReadOnlyList<string> PreValidationPreImageDoc =
    [
        "<summary>Captures a pre-image: the row as it is before the operation.</summary>",
        "<remarks>A top-level (depth 1) PreValidation step runs outside the database transaction and the image is read",
        "without a lock, so another process can change the row before the main operation. That is fine for validation;",
        "register the step at PreOperation when the logic needs a strictly consistent read. A nested PreValidation step",
        "runs inside the parent transaction and its image includes that transaction's uncommitted writes.</remarks>",
    ];

    private static readonly IReadOnlyList<string> SubordinatePreImageDoc =
    [
        "<summary>Captures a pre-image of the subordinate record: the record merged away, as it is before the merge.</summary>",
        "<remarks>Registered against the request's <c>SubordinateId</c> and read with <c>ctx.SubordinatePreImage</c>. The",
        "aliases of a step's images must all differ, master and subordinate alike.</remarks>",
    ];

    private static readonly IReadOnlyList<string> SubordinatePostImageDoc =
    [
        "<summary>Captures a post-image of the subordinate record: the record merged away, as it is after the merge",
        "(deactivated, its <c>masterid</c> pointing at the master).</summary>",
        "<remarks>Registered against the request's <c>SubordinateId</c> and read with <c>ctx.SubordinatePostImage</c>. The",
        "aliases of a step's images must all differ, master and subordinate alike.</remarks>",
    ];

    /// <summary>The fluent modifiers in declaration order. FilterOn and the images are per-message; the rest is universal.</summary>
    public IEnumerable<Modifier> Modifiers()
    {
        if (FilterOn)
        {
            yield return new("FilterOn(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)",
                "SetFilteringAttributes(attribute, additionalAttributes);");
        }
        if (Relationship)
        {
            yield return new("ForRelationship(string schemaName, params string[] additionalSchemaNames)",
                "SetRelationships(schemaName, additionalSchemaNames);");
        }
        yield return new("WithDescription(string description)", "SetDescription(description);");
        if (PreImage)
        {
            IReadOnlyList<string>? doc = Stage == Stage.PreValidation ? PreValidationPreImageDoc : null;
            yield return new($"WithPreImage({Attributes})", "AddImageNow(PluginImageAliases.PreImage, true, attributes);", doc);
            yield return new($"WithPreImage(string alias, {Attributes})", "AddImageNow(alias, true, attributes);", doc);
        }
        if (PostImage)
        {
            yield return new($"WithPostImage({Attributes})", "AddImageNow(PluginImageAliases.PostImage, false, attributes);");
            yield return new($"WithPostImage(string alias, {Attributes})", "AddImageNow(alias, false, attributes);");
        }
        if (SubordinatePreImage)
        {
            yield return new($"WithSubordinatePreImage({Attributes})", "AddSubordinateImageNow(PluginImageAliases.SubordinatePreImage, true, attributes);", SubordinatePreImageDoc);
            yield return new($"WithSubordinatePreImage(string alias, {Attributes})", "AddSubordinateImageNow(alias, true, attributes);", SubordinatePreImageDoc);
        }
        if (SubordinatePostImage)
        {
            yield return new($"WithSubordinatePostImage({Attributes})", "AddSubordinateImageNow(PluginImageAliases.SubordinatePostImage, false, attributes);", SubordinatePostImageDoc);
            yield return new($"WithSubordinatePostImage(string alias, {Attributes})", "AddSubordinateImageNow(alias, false, attributes);", SubordinatePostImageDoc);
        }
        yield return new("WithExecutionOrder(int order)", "SetExecutionOrder(order);");
        yield return new("WithStepKey(string key)", "SetKey(key);");
        yield return new("SkipWhenDepthAbove(int maxDepth)", "SetSkipWhenDepthAbove(maxDepth);");
        yield return new("WithImpersonation(Guid systemUserId)", "SetImpersonationUser(systemUserId);");
        yield return new("WithSupportedDeployment(SupportedDeployment deployment)", "SetSupportedDeployment(deployment);");
        yield return new("WithUnsecureConfig(string configuration)", "SetUnsecureConfig(configuration);");
        yield return new("WithSecureConfigFrom(string name)", "SetSecureConfigName(name);");
        yield return new("Disabled()", "SetDisabled();");
    }

    /// <summary>Stage selector interface name for a modelled message.</summary>
    public static string StagesType(string message) => $"I{message}Stages<TEntity>";

    /// <summary>Config interface name for a modelled message at a stage.</summary>
    public static string ConfigType(string message, Stage stage) => $"I{message}{stage}Config<TEntity>";

    /// <summary>The config of a modelled message (<c>On{Message}()</c>) at a stage.</summary>
    public static StepConfig ForMessage(Message message, Stage stage) => new(
        ConfigType(message.Name, stage),
        " where TEntity : Entity",
        $"Config for the {message.Name} step at {stage}. Handle(...) is last; With* modifiers are order-free.",
        stage,
        message.FilterOn,
        message.HasPreImage(stage),
        message.HasPostImage(stage),
        [new($"Action<I{message.Name}{stage.ContextSuffix()}Context<TEntity>> handler")],
        message.HasSubordinatePreImage(stage),
        message.HasSubordinatePostImage(stage),
        message.Relationship);

    /// <summary>Universal image rules for steps on a message the model does not know (OnMessage&lt;TMessage&gt; / On&lt;TRequest&gt;).</summary>
    private static readonly Message Unmodelled = new("(unmodelled)", Images: Images.PreAndPost);

    /// <summary>The config of a permissive <c>OnMessage&lt;TMessage&gt;()</c> step at a stage.</summary>
    public static StepConfig ForPermissive(Stage stage)
    {
        string context = $"IMessage{stage.ContextSuffix()}Context";
        const string typed = "<TRequest>";
        const string requestConstraint = " where TRequest : OrganizationRequest, new()";
        return new(
            $"IPermissive{stage}Config<TEntity>",
            " where TEntity : Entity",
            $"Config for an OnMessage<TMessage>() step at {stage}. Handle(...) is last; With* modifiers are order-free.",
            stage,
            FilterOn: false,
            Unmodelled.HasPreImage(stage),
            Unmodelled.HasPostImage(stage),
            [
                new("Action<ILocalPluginContext> handler"),
                new($"Action<{context}<TRequest>> handler", typed, requestConstraint),
                new($"Action<{context}<TRequest, TEntity>> handler", typed, requestConstraint),
            ]);
    }

    /// <summary>The config of a typed <c>On&lt;TRequest&gt;()</c> step at a stage (implemented by TypedStepAdapter.cs).</summary>
    public static StepConfig ForTyped(Stage stage) => new(
        $"ITyped{stage}Config<TEntity, TRequest>",
        " where TEntity : Entity where TRequest : OrganizationRequest, new()",
        $"Config for an On&lt;TRequest&gt;() step at {stage}. Handle(...) is last; With* modifiers are order-free.",
        stage,
        FilterOn: false,
        Unmodelled.HasPreImage(stage),
        Unmodelled.HasPostImage(stage),
        [new($"Action<IMessage{stage.ContextSuffix()}Context<TRequest>> handler")]);
}
