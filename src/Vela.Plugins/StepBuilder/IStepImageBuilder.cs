using System;
using System.Linq.Expressions;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Configures image attributes after PreImage() or PostImage().
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IStepImageBuilder<TEntity> where TEntity : Entity
{
    /// <summary>
    /// Specify which attributes to include in this image.
    /// </summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IStepImageBuilder<TEntity> Attributes(params Expression<Func<TEntity, object?>>[] attributes);

    /// <summary>Configure another pre-image.</summary>
    IStepImageBuilder<TEntity> PreImage();

    /// <summary>Configure another pre-image with a custom alias.</summary>
    /// <param name="alias">The image alias name.</param>
    IStepImageBuilder<TEntity> PreImage(string alias);

    /// <summary>Configure a post-image.</summary>
    IStepImageBuilder<TEntity> PostImage();

    /// <summary>Configure a post-image with a custom alias.</summary>
    /// <param name="alias">The image alias name.</param>
    IStepImageBuilder<TEntity> PostImage(string alias);

    /// <summary>Configure the execution order (back to mode-level options).</summary>
    /// <param name="order">The execution order.</param>
    IStepModeBuilder<TEntity> ExecutionOrder(int order);

    /// <summary>Configure a stable explicit key for this step (back to mode-level options).</summary>
    /// <param name="stepKey">Stable step key unique per plugin type.</param>
    IStepModeBuilder<TEntity> Key(string stepKey);

    /// <summary>Configure the handler method name for this step (back to mode-level options).</summary>
    /// <param name="methodName">The instance method name on the plugin class.</param>
    IStepModeBuilder<TEntity> Handler(string methodName);

    /// <summary>Configure the handler method for this step (back to mode-level options).</summary>
    /// <param name="handler">Instance method group with signature (ILocalPluginContext, TEntity).</param>
    IStepModeBuilder<TEntity> Handler(Action<ILocalPluginContext, TEntity> handler);

    /// <summary>Builds the step definition.</summary>
    StepDefinition Build();
}
