using System;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Terminal for a message/stage that supports a post-image only (Create in PostOperation). Exposes
/// <c>PostImage()</c> but not <c>PreImage()</c> - so a pre-image on such a step does not compile.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPostImageStep<TEntity> : IStepConfig<TEntity, IPostImageStep<TEntity>> where TEntity : Entity
{
    /// <summary>Configure a post-image for this step.</summary>
    IPostImageConfig<TEntity> PostImage();

    /// <summary>Configure a post-image with a custom alias.</summary>
    /// <param name="alias">The image alias name.</param>
    IPostImageConfig<TEntity> PostImage(string alias);

    /// <summary>Adds a post-image and immediately configures its attributes.</summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IPostImageStep<TEntity> AddPostImage(params Expression<Func<TEntity, object?>>[] attributes);
}

/// <summary>
/// Configures the attributes of the post-image just started, then returns to the post-image terminal so more
/// images or settings can be chained.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPostImageConfig<TEntity> : IPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Specify which attributes to include in this image.</summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IPostImageConfig<TEntity> Attributes(params Expression<Func<TEntity, object?>>[] attributes);
}
