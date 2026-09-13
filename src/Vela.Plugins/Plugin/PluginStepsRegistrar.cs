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

        public IQualifyLeadStepStage<TEntity> OnQualifyLead() => BeginStep().OnQualifyLead();
        public IWinOpportunityStepStage<TEntity> OnWinOpportunity() => BeginStep().OnWinOpportunity();
        public ILoseOpportunityStepStage<TEntity> OnLoseOpportunity() => BeginStep().OnLoseOpportunity();
        public IConvertQuoteToSalesOrderStepStage<TEntity> OnConvertQuoteToSalesOrder() => BeginStep().OnConvertQuoteToSalesOrder();
        public IConvertSalesOrderToInvoiceStepStage<TEntity> OnConvertSalesOrderToInvoice() => BeginStep().OnConvertSalesOrderToInvoice();
        public IGenerateQuoteFromOpportunityStepStage<TEntity> OnGenerateQuoteFromOpportunity() => BeginStep().OnGenerateQuoteFromOpportunity();
        public IGenerateSalesOrderFromOpportunityStepStage<TEntity> OnGenerateSalesOrderFromOpportunity() => BeginStep().OnGenerateSalesOrderFromOpportunity();
        public IGenerateInvoiceFromOpportunityStepStage<TEntity> OnGenerateInvoiceFromOpportunity() => BeginStep().OnGenerateInvoiceFromOpportunity();
        public IAddToQueueStepStage<TEntity> OnAddToQueue() => BeginStep().OnAddToQueue();
        public IRemoveFromQueueStepStage<TEntity> OnRemoveFromQueue() => BeginStep().OnRemoveFromQueue();
        public IPickFromQueueStepStage<TEntity> OnPickFromQueue() => BeginStep().OnPickFromQueue();
        public IRouteToStepStage<TEntity> OnRouteTo() => BeginStep().OnRouteTo();
        public IReleaseToQueueStepStage<TEntity> OnReleaseToQueue() => BeginStep().OnReleaseToQueue();
        public ISendEmailStepStage<TEntity> OnSendEmail() => BeginStep().OnSendEmail();
        public ISendEmailFromTemplateStepStage<TEntity> OnSendEmailFromTemplate() => BeginStep().OnSendEmailFromTemplate();
        public IDeliverIncomingEmailStepStage<TEntity> OnDeliverIncomingEmail() => BeginStep().OnDeliverIncomingEmail();
        public IDeliverPromoteEmailStepStage<TEntity> OnDeliverPromoteEmail() => BeginStep().OnDeliverPromoteEmail();
        public IAddMembersTeamStepStage<TEntity> OnAddMembersTeam() => BeginStep().OnAddMembersTeam();
        public IRemoveMembersTeamStepStage<TEntity> OnRemoveMembersTeam() => BeginStep().OnRemoveMembersTeam();
        public IAddListMembersListStepStage<TEntity> OnAddListMembersList() => BeginStep().OnAddListMembersList();
        public IAddMemberListStepStage<TEntity> OnAddMemberList() => BeginStep().OnAddMemberList();
        public IRemoveMemberListStepStage<TEntity> OnRemoveMemberList() => BeginStep().OnRemoveMemberList();
        public ICloseIncidentStepStage<TEntity> OnCloseIncident() => BeginStep().OnCloseIncident();
        public ICancelContractStepStage<TEntity> OnCancelContract() => BeginStep().OnCancelContract();
        public ICloneContractStepStage<TEntity> OnCloneContract() => BeginStep().OnCloneContract();
        public IRetrievePrincipalAccessStepStage<TEntity> OnRetrievePrincipalAccess() => BeginStep().OnRetrievePrincipalAccess();
        public IRetrieveSharedPrincipalsAndAccessStepStage<TEntity> OnRetrieveSharedPrincipalsAndAccess() => BeginStep().OnRetrieveSharedPrincipalsAndAccess();
        public IWhoAmIStepStage<TEntity> OnWhoAmI() => BeginStep().OnWhoAmI();
        public IWinQuoteStepStage<TEntity> OnWinQuote() => BeginStep().OnWinQuote();
        public IReviseQuoteStepStage<TEntity> OnReviseQuote() => BeginStep().OnReviseQuote();
        public IFulfillSalesOrderStepStage<TEntity> OnFulfillSalesOrder() => BeginStep().OnFulfillSalesOrder();
        public IGetQuoteProductsFromOpportunityStepStage<TEntity> OnGetQuoteProductsFromOpportunity() => BeginStep().OnGetQuoteProductsFromOpportunity();
        public IGetSalesOrderProductsFromOpportunityStepStage<TEntity> OnGetSalesOrderProductsFromOpportunity() => BeginStep().OnGetSalesOrderProductsFromOpportunity();
        public IGetInvoiceProductsFromOpportunityStepStage<TEntity> OnGetInvoiceProductsFromOpportunity() => BeginStep().OnGetInvoiceProductsFromOpportunity();

        public IPermissiveStepStage<TEntity> OnMessage(string messageName) => BeginStep().OnMessage(messageName);

        public IPermissiveStepStage<TEntity> OnMessage<TMessage>() where TMessage : class => BeginStep().OnMessage<TMessage>();

        public ITypedMessageStepStage<TEntity, TRequest> On<TRequest>() where TRequest : OrganizationRequest, new() => BeginStep().On<TRequest>();

        public IAsyncOnlyTypedMessageStepStage<TEntity, TRequest> OnAsyncOnly<TRequest>() where TRequest : OrganizationRequest, new() => BeginStep().OnAsyncOnly<TRequest>();

        private StepBuilder<TEntity> BeginStep()
        {
            StepBuilder<TEntity> step = new StepBuilder<TEntity>();
            _registerStep(step);
            return step;
        }
    }
}
