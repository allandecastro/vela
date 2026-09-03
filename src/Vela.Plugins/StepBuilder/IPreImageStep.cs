using System;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Terminal for a message/stage that supports a pre-image only (Update / Delete in PreOperation; Delete in
/// PostOperation). Exposes <c>PreImage()</c> but not <c>PostImage()</c> - so a post-image on such a step does
/// not compile.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPreImageStep<TEntity> : IStepConfig<TEntity, IPreImageStep<TEntity>> where TEntity : Entity
{
    /// <summary>Configure a pre-image for this step.</summary>
    IPreImageConfig<TEntity> PreImage();

    /// <summary>Configure a pre-image with a custom alias.</summary>
    /// <param name="alias">The image alias name.</param>
    IPreImageConfig<TEntity> PreImage(string alias);

    /// <summary>Adds a pre-image and immediately configures its attributes.</summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IPreImageStep<TEntity> AddPreImage(params Expression<Func<TEntity, object?>>[] attributes);
}

/// <summary>
/// Configures the attributes of the pre-image just started, then returns to the pre-image terminal so more
/// images or settings can be chained.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPreImageConfig<TEntity> : IPreImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Specify which attributes to include in this image.</summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IPreImageConfig<TEntity> Attributes(params Expression<Func<TEntity, object?>>[] attributes);
}
