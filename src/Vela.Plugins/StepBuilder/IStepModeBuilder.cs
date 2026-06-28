using System;
using System.Linq.Expressions;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Configures filtering attributes, images, and other options after mode selection.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IStepModeBuilder<TEntity> where TEntity : Entity
{
    /// <summary>Configure the execution order for this step.</summary>
    /// <param name="order">The execution order (lower numbers execute first).</param>
    IStepModeBuilder<TEntity> ExecutionOrder(int order);

    /// <summary>Configure a stable explicit key for this step.</summary>
    /// <param name="stepKey">Stable step key unique per plugin type.</param>
    IStepModeBuilder<TEntity> Key(string stepKey);

    /// <summary>Configure a description for this step.</summary>
    /// <param name="description">A human-readable description.</param>
    IStepModeBuilder<TEntity> Description(string description);

    /// <summary>Configure the handler method name to execute for this step.</summary>
    /// <param name="methodName">The instance method name on the plugin class.</param>
    IStepModeBuilder<TEntity> Handler(string methodName);

    /// <summary>Configure the handler method to execute for this step.</summary>
    /// <param name="handler">Instance method group with signature (ILocalPluginContext, TEntity).</param>
    IStepModeBuilder<TEntity> Handler(Action<ILocalPluginContext, TEntity> handler);

    /// <summary>Configure impersonation settings.</summary>
    /// <param name="source">The impersonation source.</param>
    IStepModeBuilder<TEntity> ImpersonateUsing(ImpersonationSource source);

    /// <summary>Configure supported deployment type.</summary>
    /// <param name="deployment">The supported deployment type.</param>
    IStepModeBuilder<TEntity> SupportedDeployment(SupportedDeployment deployment);

    /// <summary>
    /// Registers this step in the disabled (inactive) state. Steps are enabled by default; a disabled
    /// step is created but does not fire until enabled, useful as a kill-switch or for phased rollout.
    /// </summary>
    IStepModeBuilder<TEntity> Disabled();

    /// <summary>Configure the unsecure configuration string passed to the plugin at runtime.</summary>
    /// <param name="configuration">The unsecure configuration value.</param>
    IStepModeBuilder<TEntity> UnsecureConfig(string configuration);

    /// <summary>Configure the secure configuration string passed to the plugin at runtime.</summary>
    /// <param name="configuration">The secure configuration value.</param>
    IStepModeBuilder<TEntity> SecureConfig(string configuration);

    /// <summary>Configure a pre-image for this step.</summary>
    IStepImageBuilder<TEntity> PreImage();

    /// <summary>Configure a pre-image with a custom alias.</summary>
    /// <param name="alias">The image alias name.</param>
    IStepImageBuilder<TEntity> PreImage(string alias);

    /// <summary>Configure a post-image for this step.</summary>
    IStepImageBuilder<TEntity> PostImage();

    /// <summary>Configure a post-image with a custom alias.</summary>
    /// <param name="alias">The image alias name.</param>
    IStepImageBuilder<TEntity> PostImage(string alias);

    /// <summary>
    /// Adds a pre-image and immediately configures its attributes.
    /// </summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IStepModeBuilder<TEntity> AddPreImage(params Expression<Func<TEntity, object?>>[] attributes);

    /// <summary>
    /// Adds a post-image and immediately configures its attributes.
    /// </summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IStepModeBuilder<TEntity> AddPostImage(params Expression<Func<TEntity, object?>>[] attributes);

    /// <summary>Builds the step definition.</summary>
    StepDefinition Build();
}
