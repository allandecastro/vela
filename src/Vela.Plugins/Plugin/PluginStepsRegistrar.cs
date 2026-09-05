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

        public ICreateMultipleStepStage<TEntity> OnCreateMultiple() => BeginStep().OnCreateMultiple();

        public IUpdateMultipleStepStage<TEntity> OnUpdateMultiple() => BeginStep().OnUpdateMultiple();

        public IDeleteStepStage<TEntity> OnDelete() => BeginStep().OnDelete();

        public IRetrieveStepStage<TEntity> OnRetrieve() => BeginStep().OnRetrieve();

        public IRetrieveMultipleStepStage<TEntity> OnRetrieveMultiple() => BeginStep().OnRetrieveMultiple();

        public IAssociateStepStage<TEntity> OnAssociate() => BeginStep().OnAssociate();

        public IDisassociateStepStage<TEntity> OnDisassociate() => BeginStep().OnDisassociate();

        public IMergeStepStage<TEntity> OnMerge() => BeginStep().OnMerge();

        public IGrantAccessStepStage<TEntity> OnGrantAccess() => BeginStep().OnGrantAccess();

        public IModifyAccessStepStage<TEntity> OnModifyAccess() => BeginStep().OnModifyAccess();

        public IRevokeAccessStepStage<TEntity> OnRevokeAccess() => BeginStep().OnRevokeAccess();

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
