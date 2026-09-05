using System;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Terminal for a Merge step (no entity images). Extends the shared no-image terminal with a Merge-typed
/// handler: <c>.Handler(OnMerged)</c> where <c>void OnMerged(IMergeContext&lt;TEntity&gt; ctx)</c> - the wrong
/// message's handler does not compile. Settings/build come from <see cref="INoImageStep{TEntity}"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IMergeStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind this step to a typed handler method (method group), e.g. <c>.Handler(OnMerged)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IMergeContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IMergeContext<TEntity>> handler);
}

/// <summary>
/// Stage selection for Merge (no images at any stage). The handler receives an
/// <see cref="IMergeContext{TEntity}"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IMergeStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage.</summary>
    IModeSelector<IMergeStep<TEntity>> PreValidation();

    /// <summary>Register in the PreOperation stage.</summary>
    IModeSelector<IMergeStep<TEntity>> PreOperation();

    /// <summary>Register in the PostOperation stage.</summary>
    IModeSelector<IMergeStep<TEntity>> PostOperation();
}
