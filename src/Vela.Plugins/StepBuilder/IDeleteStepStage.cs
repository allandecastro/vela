using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Stage selection for Delete. Delete has no post-image (the row is gone); only a pre-image is available,
/// in PreOperation and PostOperation. No filtering attributes (Delete carries no attribute payload).
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IDeleteStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage (outside the transaction, before security checks).</summary>
    IModeSelector<INoImageStep<TEntity>> PreValidation();

    /// <summary>Register in the PreOperation stage (inside the transaction, before the delete).</summary>
    IModeSelector<IPreImageStep<TEntity>> PreOperation();

    /// <summary>Register in the PostOperation stage (inside the transaction, after the delete).</summary>
    IModeSelector<IPreImageStep<TEntity>> PostOperation();
}
