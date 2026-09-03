using System;
using Vela.StepBuilder;
using Microsoft.Xrm.Sdk;

namespace Vela.Plugin
{
    internal sealed class PluginStepsRegistrar<TEntity>(Action<StepBuilder<TEntity>> registerStep) : IStepBuilder<TEntity>
        where TEntity : Entity
    {
        private readonly Action<StepBuilder<TEntity>> _registerStep = registerStep ?? throw new ArgumentNullException(nameof(registerStep));

        public ICreateStepStage<TEntity> OnCreate() => BeginStep().OnCreate();

        public IUpdateStepStage<TEntity> OnUpdate() => BeginStep().OnUpdate();

        public ICreateStepStage<TEntity> OnCreateMultiple() => BeginStep().OnCreateMultiple();

        public IUpdateStepStage<TEntity> OnUpdateMultiple() => BeginStep().OnUpdateMultiple();

        public IDeleteStepStage<TEntity> OnDelete() => BeginStep().OnDelete();

        public IReferenceStepStage<TEntity> OnRetrieve() => BeginStep().OnRetrieve();

        public IReferenceStepStage<TEntity> OnRetrieveMultiple() => BeginStep().OnRetrieveMultiple();

        public IReferenceStepStage<TEntity> OnAssociate() => BeginStep().OnAssociate();

        public IReferenceStepStage<TEntity> OnDisassociate() => BeginStep().OnDisassociate();

        public IPermissiveStepStage<TEntity> OnMessage(string messageName) => BeginStep().OnMessage(messageName);

        public IPermissiveStepStage<TEntity> OnMessage<TMessage>() where TMessage : class => BeginStep().OnMessage<TMessage>();

        private StepBuilder<TEntity> BeginStep()
        {
            StepBuilder<TEntity> step = new StepBuilder<TEntity>();
            _registerStep(step);
            return step;
        }
    }
}
