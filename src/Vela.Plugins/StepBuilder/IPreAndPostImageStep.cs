using System;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Terminal for a message/stage that supports both a pre-image and a post-image (Update in PostOperation;
/// also the permissive <c>OnMessage(...)</c> PostOperation terminal). Exposes both <c>PreImage()</c> and
/// <c>PostImage()</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPreAndPostImageStep<TEntity> : IStepConfig<TEntity, IPreAndPostImageStep<TEntity>> where TEntity : Entity
{
    /// <summary>Configure a pre-image for this step.</summary>
    IPreAndPostImageConfig<TEntity> PreImage();

    /// <summary>Configure a pre-image with a custom alias.</summary>
    /// <param name="alias">The image alias name.</param>
    IPreAndPostImageConfig<TEntity> PreImage(string alias);

    /// <summary>Configure a post-image for this step.</summary>
    IPreAndPostImageConfig<TEntity> PostImage();

    /// <summary>Configure a post-image with a custom alias.</summary>
    /// <param name="alias">The image alias name.</param>
    IPreAndPostImageConfig<TEntity> PostImage(string alias);

    /// <summary>Adds a pre-image and immediately configures its attributes.</summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IPreAndPostImageStep<TEntity> AddPreImage(params Expression<Func<TEntity, object?>>[] attributes);

    /// <summary>Adds a post-image and immediately configures its attributes.</summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IPreAndPostImageStep<TEntity> AddPostImage(params Expression<Func<TEntity, object?>>[] attributes);
}

/// <summary>
/// Configures the attributes of the image just started, then returns to the pre-and-post terminal so the
/// other image or more settings can be chained.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPreAndPostImageConfig<TEntity> : IPreAndPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Specify which attributes to include in this image.</summary>
    /// <param name="attributes">Lambda expressions selecting entity attributes.</param>
    IPreAndPostImageConfig<TEntity> Attributes(params Expression<Func<TEntity, object?>>[] attributes);
}
