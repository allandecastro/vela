using Vela.Constants;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Configures the mode after stage selection.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IStepStageBuilder<TEntity> where TEntity : Entity
{
    /// <summary>Configure the execution mode (synchronous or asynchronous).</summary>
    /// <param name="mode">The execution mode.</param>
    IStepModeBuilder<TEntity> Mode(ExecutionMode mode);
}
