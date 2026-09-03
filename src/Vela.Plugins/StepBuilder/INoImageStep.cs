using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Terminal for a message/stage that supports no entity images (PreValidation on any message; PreOperation
/// on Create; any stage on Retrieve / RetrieveMultiple / Associate / Disassociate). Exposes settings and
/// build, but no <c>PreImage()</c> / <c>PostImage()</c> - so an image on such a step does not compile.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface INoImageStep<TEntity> : IStepConfig<TEntity, INoImageStep<TEntity>> where TEntity : Entity
{
}
