using System;
using Vela.StepBuilder;
using Microsoft.Xrm.Sdk;

namespace Vela.Plugin
{
    /// <summary>
    /// Registrar handed to a plugin's <c>RegisterPluginSteps()</c>: each <c>RegisterStep()</c> call starts a
    /// fresh <see cref="StepBuilder{TEntity}"/> and registers it by side-effect. The fluent trigger surface
    /// (the <c>Vela.Steps.IStepTrigger&lt;TEntity&gt;</c> implementation that delegates every <c>On...()</c> to
    /// <see cref="BeginStep"/>) lives in the generated partial (<c>Steps/StepBuilder.Impl.cs</c>).
    /// </summary>
    internal sealed partial class PluginStepsRegistrar<TEntity>(Action<StepBuilder<TEntity>> registerStep)
        where TEntity : Entity
    {
        private readonly Action<StepBuilder<TEntity>> _registerStep = registerStep ?? throw new ArgumentNullException(nameof(registerStep));

        private StepBuilder<TEntity> BeginStep()
        {
            StepBuilder<TEntity> step = new StepBuilder<TEntity>();
            _registerStep(step);
            return step;
        }
    }
}
