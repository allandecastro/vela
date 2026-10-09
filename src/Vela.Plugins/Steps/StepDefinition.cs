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

    /// <summary>
    /// The sdkmessage the step registers on and sees as <c>MessageName</c> at runtime (Create, Update, Send, Win,
    /// ...). For a few business messages this differs from the SDK request name the step was declared with
    /// (<c>OnSendEmail()</c> registers on <c>Send</c>); see <see cref="RequestName"/>.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The SDK request name the step was declared with (e.g. <c>WinQuote</c>, while <see cref="Message"/> is
    /// <c>Win</c>). The runtime builds the typed context from it, since one sdkmessage can serve several requests.
    /// Empty when the step was not declared through the builder; <see cref="Message"/> is then used.
    /// </summary>
    internal string RequestName { get; set; } = string.Empty;

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
