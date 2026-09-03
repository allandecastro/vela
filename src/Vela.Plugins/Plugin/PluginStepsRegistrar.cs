using System;
using Vela.StepBuilder;
using Microsoft.Xrm.Sdk;

namespace Vela.Plugin
{
    internal sealed class PluginStepsRegistrar<TEntity>(Action<StepBuilder<TEntity>> registerStep) : IStepBuilder<TEntity>
        where TEntity : Entity
    {
        private readonly Action<StepBuilder<TEntity>> _registerStep = registerStep ?? throw new ArgumentNullException(nameof(registerStep));

        public IFilteringStepMessageBuilder<TEntity> OnCreate()
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnCreate();
        }

        public IFilteringStepMessageBuilder<TEntity> OnUpdate()
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnUpdate();
        }

        public IFilteringStepMessageBuilder<TEntity> OnCreateMultiple()
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnCreateMultiple();
        }

        public IFilteringStepMessageBuilder<TEntity> OnUpdateMultiple()
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnUpdateMultiple();
        }

        public IStepMessageBuilder<TEntity> OnDelete()
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnDelete();
        }

        public IStepMessageBuilder<TEntity> OnRetrieve()
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnRetrieve();
        }

        public IStepMessageBuilder<TEntity> OnRetrieveMultiple()
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnRetrieveMultiple();
        }

        public IStepMessageBuilder<TEntity> OnAssociate()
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnAssociate();
        }

        public IStepMessageBuilder<TEntity> OnDisassociate()
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnDisassociate();
        }

        public IStepMessageBuilder<TEntity> OnMessage(string messageName)
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnMessage(messageName);
        }

        public IStepMessageBuilder<TEntity> OnMessage<TMessage>() where TMessage : class
        {
            StepBuilder<TEntity> step = BeginStep();
            return step.OnMessage<TMessage>();
        }

        private StepBuilder<TEntity> BeginStep()
        {
            StepBuilder<TEntity> step = new StepBuilder<TEntity>();
            _registerStep(step);
            return step;
        }
    }
}
