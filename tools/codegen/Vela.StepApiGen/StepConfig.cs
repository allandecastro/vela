namespace Vela.StepApiGen;

/// <summary>A config-interface member that returns the config itself (fluent, order-free).</summary>
/// <param name="Signature">Name and parameter list, e.g. <c>WithStepKey(string key)</c>.</param>
/// <param name="Body">The StepBuilder statement that applies it, e.g. <c>SetKey(key);</c>.</param>
internal sealed record Modifier(string Signature, string Body);

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
    bool FilterOn,
    bool PreImage,
    bool PostImage,
    IReadOnlyList<HandleOverload> Handles)
{
    public const string Attributes = "params Expression<Func<TEntity, object?>>[] attributes";

    /// <summary>The fluent modifiers in declaration order. FilterOn and the images are per-message; the rest is universal.</summary>
    public IEnumerable<Modifier> Modifiers()
    {
        if (FilterOn)
        {
            yield return new("FilterOn(Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)",
                "SetFilteringAttributes(attribute, additionalAttributes);");
        }
        yield return new("WithDescription(string description)", "SetDescription(description);");
        if (PreImage)
        {
            yield return new($"WithPreImage({Attributes})", "AddImageNow(PluginImageAliases.PreImage, true, attributes);");
            yield return new($"WithPreImage(string alias, {Attributes})", "AddImageNow(alias, true, attributes);");
        }
        if (PostImage)
        {
            yield return new($"WithPostImage({Attributes})", "AddImageNow(PluginImageAliases.PostImage, false, attributes);");
            yield return new($"WithPostImage(string alias, {Attributes})", "AddImageNow(alias, false, attributes);");
        }
        yield return new("WithExecutionOrder(int order)", "SetExecutionOrder(order);");
        yield return new("WithStepKey(string key)", "SetKey(key);");
        yield return new("WithImpersonation(ImpersonationSource source)", "SetImpersonation(source);");
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
        $"Config for a {message.Name} step at {stage}. Handle(...) is last; With* modifiers are order-free.",
        message.FilterOn,
        message.HasPreImage(stage),
        message.HasPostImage(stage),
        [new($"Action<I{message.Name}{stage.ContextSuffix()}Context<TEntity>> handler")]);

    /// <summary>Universal image rules for steps on a message the model does not know (OnMessage / On&lt;TRequest&gt;).</summary>
    private static readonly Message Unmodelled = new("(unmodelled)", Images: Images.PreAndPost);

    /// <summary>The config of a permissive <c>OnMessage(...)</c> step at a stage.</summary>
    public static StepConfig ForPermissive(Stage stage)
    {
        string context = $"IMessage{stage.ContextSuffix()}Context";
        const string typed = "<TRequest>";
        const string requestConstraint = " where TRequest : OrganizationRequest, new()";
        return new(
            $"IPermissive{stage}Config<TEntity>",
            " where TEntity : Entity",
            $"Config for an OnMessage(...) step at {stage}. Handle(...) is last; With* modifiers are order-free.",
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
        FilterOn: false,
        Unmodelled.HasPreImage(stage),
        Unmodelled.HasPostImage(stage),
        [new($"Action<IMessage{stage.ContextSuffix()}Context<TRequest>> handler")]);
}
