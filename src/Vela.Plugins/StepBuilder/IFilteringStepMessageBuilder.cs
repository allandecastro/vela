using Vela.Constants;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Stage selection for a message that requires filtering attributes (Create / Update). Mirrors
/// <see cref="IStepMessageBuilder{TEntity}"/> but routes through a chain that forces at least one
/// filtering attribute before the step can be built.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IFilteringStepMessageBuilder<TEntity> where TEntity : Entity
{
    /// <summary>Configure the plugin stage (PreValidation, PreOperation, PostOperation).</summary>
    /// <param name="stage">The plugin execution stage.</param>
    IFilteringStepStageBuilder<TEntity> Stage(PluginStages stage);
}
