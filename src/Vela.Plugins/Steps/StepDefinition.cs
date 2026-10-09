using System;
using Vela.Constants;

namespace Vela.Steps;

/// <summary>
/// Represents the complete configuration for a plugin step registration.
/// Used for generating registration manifests and scripts.
/// </summary>
public sealed class StepDefinition
{
    /// <summary>The plugin type full name.</summary>
    public string PluginTypeName { get; set; } = string.Empty;

    /// <summary>The entity logical name.</summary>
    public string EntityLogicalName { get; set; } = string.Empty;

    /// <summary>The SDK message name (Create, Update, Delete, etc.).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The plugin execution stage.</summary>
    public PluginStages Stage { get; set; }

    /// <summary>The execution mode (sync or async).</summary>
    public ExecutionMode Mode { get; set; }

    /// <summary>The filtering attributes (triggers plugin only when these change).</summary>
    public string[] FilteringAttributes { get; set; } = [];

    /// <summary>The execution order for this step.</summary>
    public int ExecutionOrder { get; set; } = 1;

    /// <summary>Stable explicit key used to correlate this step across deployments.</summary>
    public string StepKey { get; set; } = string.Empty;

    /// <summary>A description for this step.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The plugin method name to invoke for this step.</summary>
    public string HandlerMethodName { get; set; } = string.Empty;

    /// <summary>The impersonation source.</summary>
    public ImpersonationSource ImpersonationSource { get; set; }

    /// <summary>
    /// The specific systemuser to impersonate for this step (Dataverse <c>impersonatinguserid</c>), set with
    /// <c>.WithImpersonation(Guid)</c>. When set, the step runs in that user's context; <c>null</c> (the default)
    /// means no specific-user impersonation (the step runs as the calling user).
    /// </summary>
    public Guid? ImpersonatingUserId { get; set; }

    /// <summary>The supported deployment type.</summary>
    public SupportedDeployment SupportedDeployment { get; set; }

    /// <summary>The pre-image configurations.</summary>
    public ImageDefinition[] PreImages { get; set; } = [];

    /// <summary>The post-image configurations.</summary>
    public ImageDefinition[] PostImages { get; set; } = [];

    /// <summary>
    /// Whether the step is enabled (active) once registered. Defaults to <c>true</c>; mark a step
    /// disabled with <c>.Disabled()</c> to register it as a dormant kill-switch / phased rollout.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The unsecure configuration string passed to the plugin constructor.</summary>
    public string UnsecureConfig { get; set; } = string.Empty;

    /// <summary>
    /// The name of the step's secure configuration, or empty when the step declares none. Only the name is part of
    /// the definition: <c>vela apply</c> resolves the value at deployment time and passes it to the plugin
    /// constructor at runtime, so the secret is never compiled into the assembly.
    /// </summary>
    public string SecureConfigName { get; set; } = string.Empty;

    /// <summary>
    /// The modelled message of a typed <c>On{Message}()</c> trigger (<c>WinQuote</c> for <c>OnWinQuote()</c>), which
    /// picks the handler's typed context. It differs from <see cref="Message"/> (the sdkmessage, <c>Win</c>) when
    /// several SDK requests share one sdkmessage. Empty for a permissive or <c>On&lt;TRequest&gt;()</c> step.
    /// </summary>
    internal string ModelledMessage { get; set; } = string.Empty;
}

/// <summary>
/// Represents the configuration for a plugin image registration.
/// </summary>
public sealed class ImageDefinition
{
    /// <summary>The image alias name.</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>The attributes to include in the image.</summary>
    public string[] Attributes { get; set; } = [];
}
