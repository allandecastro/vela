using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Stage selection for a custom / non-modelled message (<c>OnMessage(...)</c>). Vela cannot know the image
/// rules of an arbitrary message, so only the universal stage rules apply at compile time: no image in
/// PreValidation, a pre-image from PreOperation, and both images in PostOperation. Message-specific validity
/// (for example a platform message that supports no image) is left to the runtime backstop / the platform at
/// deploy time - Vela does not guess.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IPermissiveStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage (outside the transaction, before security checks).</summary>
    IModeSelector<INoImageStep<TEntity>> PreValidation();

    /// <summary>Register in the PreOperation stage (inside the transaction, before the operation).</summary>
    IModeSelector<IPreImageStep<TEntity>> PreOperation();

    /// <summary>Register in the PostOperation stage (inside the transaction, after the operation).</summary>
    IModeSelector<IPreAndPostImageStep<TEntity>> PostOperation();
}
