using System;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Common step settings, shared by every terminal in the typed step chain. Each setter returns
/// <typeparamref name="TSelf"/> (the capability-specific terminal), so settings can be chained before or
/// after images without losing access to the images the current message/stage actually supports.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
/// <typeparam name="TSelf">The concrete terminal interface returned by each setter (CRTP).</typeparam>
public interface IStepConfig<TEntity, TSelf> where TEntity : Entity
{
    /// <summary>Configure the execution order for this step.</summary>
    /// <param name="order">The execution order (lower numbers execute first).</param>
    TSelf ExecutionOrder(int order);

    /// <summary>Configure a stable explicit key for this step.</summary>
    /// <param name="stepKey">Stable step key unique per plugin type.</param>
    TSelf Key(string stepKey);

    /// <summary>Configure a description for this step.</summary>
    /// <param name="description">A human-readable description.</param>
    TSelf Description(string description);

    /// <summary>Configure the handler method name to execute for this step.</summary>
    /// <param name="methodName">The instance method name on the plugin class.</param>
    TSelf Handler(string methodName);

    /// <summary>Configure the handler method to execute for this step.</summary>
    /// <param name="handler">Instance method group with signature (ILocalPluginContext, TEntity).</param>
    TSelf Handler(Action<ILocalPluginContext, TEntity> handler);

    /// <summary>Configure impersonation settings.</summary>
    /// <param name="source">The impersonation source.</param>
    TSelf ImpersonateUsing(ImpersonationSource source);

    /// <summary>Configure supported deployment type.</summary>
    /// <param name="deployment">The supported deployment type.</param>
    TSelf SupportedDeployment(SupportedDeployment deployment);

    /// <summary>
    /// Registers this step in the disabled (inactive) state. Steps are enabled by default; a disabled
    /// step is created but does not fire until enabled, useful as a kill-switch or for phased rollout.
    /// </summary>
    TSelf Disabled();

    /// <summary>Configure the unsecure configuration string passed to the plugin at runtime.</summary>
    /// <param name="configuration">The unsecure configuration value.</param>
    TSelf UnsecureConfig(string configuration);

    /// <summary>Configure the secure configuration string passed to the plugin at runtime.</summary>
    /// <param name="configuration">The secure configuration value.</param>
    TSelf SecureConfig(string configuration);

    /// <summary>Builds the step definition.</summary>
    StepDefinition Build();
}
