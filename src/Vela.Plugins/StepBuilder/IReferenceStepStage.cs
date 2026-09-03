using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Stage selection for messages that never carry entity images: Retrieve, RetrieveMultiple, Associate,
/// Disassociate. No image is available at any stage.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IReferenceStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage (outside the transaction, before security checks).</summary>
    IModeSelector<INoImageStep<TEntity>> PreValidation();

    /// <summary>Register in the PreOperation stage (inside the transaction, before the operation).</summary>
    IModeSelector<INoImageStep<TEntity>> PreOperation();

    /// <summary>Register in the PostOperation stage (inside the transaction, after the operation).</summary>
    IModeSelector<INoImageStep<TEntity>> PostOperation();
}
