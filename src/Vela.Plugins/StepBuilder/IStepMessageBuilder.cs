using Vela.Constants;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Configures the stage after message selection.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IStepMessageBuilder<TEntity> where TEntity : Entity
{
    /// <summary>Configure the plugin stage (PreValidation, PreOperation, PostOperation).</summary>
    /// <param name="stage">The plugin execution stage.</param>
    IStepStageBuilder<TEntity> Stage(PluginStages stage);
}
