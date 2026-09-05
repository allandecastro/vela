using System;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

// Per-message typed steps for the messages that carry images (Delete, Create/CreateMultiple,
// Update/UpdateMultiple). Each variant extends the matching shared image terminal (INoImageStep /
// IPreImageStep / IPostImageStep / IPreAndPostImageStep) and adds the message-typed .Handler; the image
// starters and settings are inherited from the shared terminal, so they are not duplicated.

// ---- Delete (no filtering; NoImage in PreValidation, PreImage in Pre/PostOperation) -----------------

/// <summary>Delete step at a stage with no image.</summary>
public interface IDeleteNoImageStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDeleted)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDeleteContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDeleteContext<TEntity>> handler);
}

/// <summary>Delete step at a stage that supports a pre-image.</summary>
public interface IDeletePreImageStep<TEntity> : IPreImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDeleted)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDeleteContext&lt;TEntity&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler(Action<IDeleteContext<TEntity>> handler);
}

/// <summary>Stage selection for Delete.</summary>
public interface IDeleteStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation (no image).</summary>
    IModeSelector<IDeleteNoImageStep<TEntity>> PreValidation();
    /// <summary>PreOperation (pre-image).</summary>
    IModeSelector<IDeletePreImageStep<TEntity>> PreOperation();
    /// <summary>PostOperation (pre-image).</summary>
    IModeSelector<IDeletePreImageStep<TEntity>> PostOperation();
}

// ---- Create / CreateMultiple (filtering; NoImage in Pre*, PostImage in PostOperation) ----------------

/// <summary>Create step at a stage with no image.</summary>
public interface ICreateNoImageStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(ICreateContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICreateContext<TEntity>> handler);
}

/// <summary>Create step at a stage that supports a post-image.</summary>
public interface ICreatePostImageStep<TEntity> : IPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(ICreateContext&lt;TEntity&gt;)</c>.</param>
    IPostImageStep<TEntity> Handler(Action<ICreateContext<TEntity>> handler);
}

/// <summary>Stage selection for Create (filtering required after mode).</summary>
public interface ICreateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation (no image).</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreateNoImageStep<TEntity>>> PreValidation();
    /// <summary>PreOperation (no image).</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreateNoImageStep<TEntity>>> PreOperation();
    /// <summary>PostOperation (post-image).</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreatePostImageStep<TEntity>>> PostOperation();
}

/// <summary>CreateMultiple step at a stage with no image.</summary>
public interface ICreateMultipleNoImageStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(ICreateMultipleContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICreateMultipleContext<TEntity>> handler);
}

/// <summary>CreateMultiple step at a stage that supports a post-image.</summary>
public interface ICreateMultiplePostImageStep<TEntity> : IPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(ICreateMultipleContext&lt;TEntity&gt;)</c>.</param>
    IPostImageStep<TEntity> Handler(Action<ICreateMultipleContext<TEntity>> handler);
}

/// <summary>Stage selection for CreateMultiple (filtering required after mode).</summary>
public interface ICreateMultipleStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation (no image).</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreateMultipleNoImageStep<TEntity>>> PreValidation();
    /// <summary>PreOperation (no image).</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreateMultipleNoImageStep<TEntity>>> PreOperation();
    /// <summary>PostOperation (post-image).</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePostImageStep<TEntity>>> PostOperation();
}

// ---- Update / UpdateMultiple (filtering; NoImage PreVal, PreImage PreOp, Pre+Post PostOp) ------------

/// <summary>Update step at a stage with no image.</summary>
public interface IUpdateNoImageStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(IUpdateContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IUpdateContext<TEntity>> handler);
}

/// <summary>Update step at a stage that supports a pre-image.</summary>
public interface IUpdatePreImageStep<TEntity> : IPreImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(IUpdateContext&lt;TEntity&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler(Action<IUpdateContext<TEntity>> handler);
}

/// <summary>Update step at a stage that supports a pre-image and a post-image.</summary>
public interface IUpdatePreAndPostStep<TEntity> : IPreAndPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(IUpdateContext&lt;TEntity&gt;)</c>.</param>
    IPreAndPostImageStep<TEntity> Handler(Action<IUpdateContext<TEntity>> handler);
}

/// <summary>Stage selection for Update (filtering required after mode).</summary>
public interface IUpdateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation (no image).</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdateNoImageStep<TEntity>>> PreValidation();
    /// <summary>PreOperation (pre-image).</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreImageStep<TEntity>>> PreOperation();
    /// <summary>PostOperation (pre-image and post-image).</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreAndPostStep<TEntity>>> PostOperation();
}

/// <summary>UpdateMultiple step at a stage with no image.</summary>
public interface IUpdateMultipleNoImageStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(IUpdateMultipleContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IUpdateMultipleContext<TEntity>> handler);
}

/// <summary>UpdateMultiple step at a stage that supports a pre-image.</summary>
public interface IUpdateMultiplePreImageStep<TEntity> : IPreImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(IUpdateMultipleContext&lt;TEntity&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler(Action<IUpdateMultipleContext<TEntity>> handler);
}

/// <summary>UpdateMultiple step at a stage that supports a pre-image and a post-image.</summary>
public interface IUpdateMultiplePreAndPostStep<TEntity> : IPreAndPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary><param name="handler"><c>(IUpdateMultipleContext&lt;TEntity&gt;)</c>.</param>
    IPreAndPostImageStep<TEntity> Handler(Action<IUpdateMultipleContext<TEntity>> handler);
}

/// <summary>Stage selection for UpdateMultiple (filtering required after mode).</summary>
public interface IUpdateMultipleStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation (no image).</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultipleNoImageStep<TEntity>>> PreValidation();
    /// <summary>PreOperation (pre-image).</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreImageStep<TEntity>>> PreOperation();
    /// <summary>PostOperation (pre-image and post-image).</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreAndPostStep<TEntity>>> PostOperation();
}
