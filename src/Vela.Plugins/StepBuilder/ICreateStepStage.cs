using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Stage selection for Create / CreateMultiple. Create has no pre-image at any stage (there is no prior
/// state); only PostOperation captures a post-image. Filtering attributes are required after the mode.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface ICreateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage (outside the transaction, before security checks).</summary>
    IModeSelector<IFilteringSelector<TEntity, INoImageStep<TEntity>>> PreValidation();

    /// <summary>Register in the PreOperation stage (inside the transaction, before the write).</summary>
    IModeSelector<IFilteringSelector<TEntity, INoImageStep<TEntity>>> PreOperation();

    /// <summary>Register in the PostOperation stage (inside the transaction, after the write).</summary>
    IModeSelector<IFilteringSelector<TEntity, IPostImageStep<TEntity>>> PostOperation();
}
