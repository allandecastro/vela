using Vela.Constants;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Mode selection for a message that requires filtering attributes (Create / Update).
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IFilteringStepStageBuilder<TEntity> where TEntity : Entity
{
    /// <summary>Configure the execution mode (synchronous or asynchronous).</summary>
    /// <param name="mode">The execution mode.</param>
    IFilteringStepModeBuilder<TEntity> Mode(ExecutionMode mode);
}
