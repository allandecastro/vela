using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vela.Context;
using Vela.Constants;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.StepBuilder;

/// <summary>
/// Fluent builder for configuring plugin step registration. Implements the whole typed step chain
/// (message -> stage -> mode -> filtering -> settings/images); most transition methods are explicit
/// interface implementations returning <c>this</c>, so a single mutable builder walks the type-state.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public sealed class StepBuilder<TEntity> :
    IStepBuilder<TEntity>,
    IPermissiveStepStage<TEntity>,
    IModeSelector<IPermissiveNoImageStep<TEntity>>, IPermissiveNoImageStep<TEntity>,
    IModeSelector<IPermissivePreImageStep<TEntity>>, IPermissivePreImageStep<TEntity>,
    IModeSelector<IPermissivePreAndPostStep<TEntity>>, IPermissivePreAndPostStep<TEntity>,
    ICreateStepStage<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, ICreatePreStep<TEntity>>>, IFilteringSelector<TEntity, ICreatePreStep<TEntity>>, ICreatePreStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, ICreatePostStep<TEntity>>>, IFilteringSelector<TEntity, ICreatePostStep<TEntity>>, ICreatePostStep<TEntity>,
    IUpdateStepStage<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreValStep<TEntity>>>, IFilteringSelector<TEntity, IUpdatePreValStep<TEntity>>, IUpdatePreValStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreOpStep<TEntity>>>, IFilteringSelector<TEntity, IUpdatePreOpStep<TEntity>>, IUpdatePreOpStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdatePostStep<TEntity>>>, IFilteringSelector<TEntity, IUpdatePostStep<TEntity>>, IUpdatePostStep<TEntity>,
    IDeleteStepStage<TEntity>,
    IModeSelector<IDeletePreValStep<TEntity>>, IDeletePreValStep<TEntity>,
    IModeSelector<IDeletePreOpStep<TEntity>>, IDeletePreOpStep<TEntity>,
    IModeSelector<IDeletePostStep<TEntity>>, IDeletePostStep<TEntity>,
    ICreateMultipleStepStage<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePreStep<TEntity>>>, IFilteringSelector<TEntity, ICreateMultiplePreStep<TEntity>>, ICreateMultiplePreStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePostStep<TEntity>>>, IFilteringSelector<TEntity, ICreateMultiplePostStep<TEntity>>, ICreateMultiplePostStep<TEntity>,
    IUpdateMultipleStepStage<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreValStep<TEntity>>>, IFilteringSelector<TEntity, IUpdateMultiplePreValStep<TEntity>>, IUpdateMultiplePreValStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreOpStep<TEntity>>>, IFilteringSelector<TEntity, IUpdateMultiplePreOpStep<TEntity>>, IUpdateMultiplePreOpStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePostStep<TEntity>>>, IFilteringSelector<TEntity, IUpdateMultiplePostStep<TEntity>>, IUpdateMultiplePostStep<TEntity>,
    IRetrieveStepStage<TEntity>,
    IModeSelector<IRetrievePreStep<TEntity>>, IRetrievePreStep<TEntity>,
    IModeSelector<IRetrievePostStep<TEntity>>, IRetrievePostStep<TEntity>,
    IRetrieveMultipleStepStage<TEntity>,
    IModeSelector<IRetrieveMultiplePreStep<TEntity>>, IRetrieveMultiplePreStep<TEntity>,
    IModeSelector<IRetrieveMultiplePostStep<TEntity>>, IRetrieveMultiplePostStep<TEntity>,
    IAssociateStepStage<TEntity>,
    IModeSelector<IAssociatePreStep<TEntity>>, IAssociatePreStep<TEntity>,
    IModeSelector<IAssociatePostStep<TEntity>>, IAssociatePostStep<TEntity>,
    IDisassociateStepStage<TEntity>,
    IModeSelector<IDisassociatePreStep<TEntity>>, IDisassociatePreStep<TEntity>,
    IModeSelector<IDisassociatePostStep<TEntity>>, IDisassociatePostStep<TEntity>,
    IMergeStepStage<TEntity>,
    IModeSelector<IMergePreStep<TEntity>>, IMergePreStep<TEntity>,
    IModeSelector<IMergePostStep<TEntity>>, IMergePostStep<TEntity>,
    IGrantAccessStepStage<TEntity>,
    IModeSelector<IGrantAccessPreStep<TEntity>>, IGrantAccessPreStep<TEntity>,
    IModeSelector<IGrantAccessPostStep<TEntity>>, IGrantAccessPostStep<TEntity>,
    IModifyAccessStepStage<TEntity>,
    IModeSelector<IModifyAccessPreStep<TEntity>>, IModifyAccessPreStep<TEntity>,
    IModeSelector<IModifyAccessPostStep<TEntity>>, IModifyAccessPostStep<TEntity>,
    IRevokeAccessStepStage<TEntity>,
    IModeSelector<IRevokeAccessPreStep<TEntity>>, IRevokeAccessPreStep<TEntity>,
    IModeSelector<IRevokeAccessPostStep<TEntity>>, IRevokeAccessPostStep<TEntity>,
    IQualifyLeadStepStage<TEntity>,
    IModeSelector<IQualifyLeadPreStep<TEntity>>, IQualifyLeadPreStep<TEntity>,
    IModeSelector<IQualifyLeadPostStep<TEntity>>, IQualifyLeadPostStep<TEntity>,
    IWinOpportunityStepStage<TEntity>,
    IModeSelector<IWinOpportunityPreStep<TEntity>>, IWinOpportunityPreStep<TEntity>,
    IModeSelector<IWinOpportunityPostStep<TEntity>>, IWinOpportunityPostStep<TEntity>,
    ILoseOpportunityStepStage<TEntity>,
    IModeSelector<ILoseOpportunityPreStep<TEntity>>, ILoseOpportunityPreStep<TEntity>,
    IModeSelector<ILoseOpportunityPostStep<TEntity>>, ILoseOpportunityPostStep<TEntity>,
    IConvertQuoteToSalesOrderStepStage<TEntity>,
    IModeSelector<IConvertQuoteToSalesOrderPreStep<TEntity>>, IConvertQuoteToSalesOrderPreStep<TEntity>,
    IModeSelector<IConvertQuoteToSalesOrderPostStep<TEntity>>, IConvertQuoteToSalesOrderPostStep<TEntity>,
    IConvertSalesOrderToInvoiceStepStage<TEntity>,
    IModeSelector<IConvertSalesOrderToInvoicePreStep<TEntity>>, IConvertSalesOrderToInvoicePreStep<TEntity>,
    IModeSelector<IConvertSalesOrderToInvoicePostStep<TEntity>>, IConvertSalesOrderToInvoicePostStep<TEntity>,
    IGenerateQuoteFromOpportunityStepStage<TEntity>,
    IModeSelector<IGenerateQuoteFromOpportunityPreStep<TEntity>>, IGenerateQuoteFromOpportunityPreStep<TEntity>,
    IModeSelector<IGenerateQuoteFromOpportunityPostStep<TEntity>>, IGenerateQuoteFromOpportunityPostStep<TEntity>,
    IGenerateSalesOrderFromOpportunityStepStage<TEntity>,
    IModeSelector<IGenerateSalesOrderFromOpportunityPreStep<TEntity>>, IGenerateSalesOrderFromOpportunityPreStep<TEntity>,
    IModeSelector<IGenerateSalesOrderFromOpportunityPostStep<TEntity>>, IGenerateSalesOrderFromOpportunityPostStep<TEntity>,
    IGenerateInvoiceFromOpportunityStepStage<TEntity>,
    IModeSelector<IGenerateInvoiceFromOpportunityPreStep<TEntity>>, IGenerateInvoiceFromOpportunityPreStep<TEntity>,
    IModeSelector<IGenerateInvoiceFromOpportunityPostStep<TEntity>>, IGenerateInvoiceFromOpportunityPostStep<TEntity>,
    IAddToQueueStepStage<TEntity>,
    IModeSelector<IAddToQueuePreStep<TEntity>>, IAddToQueuePreStep<TEntity>,
    IModeSelector<IAddToQueuePostStep<TEntity>>, IAddToQueuePostStep<TEntity>,
    IRemoveFromQueueStepStage<TEntity>,
    IModeSelector<IRemoveFromQueuePreStep<TEntity>>, IRemoveFromQueuePreStep<TEntity>,
    IModeSelector<IRemoveFromQueuePostStep<TEntity>>, IRemoveFromQueuePostStep<TEntity>,
    IPickFromQueueStepStage<TEntity>,
    IModeSelector<IPickFromQueuePreStep<TEntity>>, IPickFromQueuePreStep<TEntity>,
    IModeSelector<IPickFromQueuePostStep<TEntity>>, IPickFromQueuePostStep<TEntity>,
    IRouteToStepStage<TEntity>,
    IModeSelector<IRouteToPreStep<TEntity>>, IRouteToPreStep<TEntity>,
    IModeSelector<IRouteToPostStep<TEntity>>, IRouteToPostStep<TEntity>,
    IReleaseToQueueStepStage<TEntity>,
    IModeSelector<IReleaseToQueuePreStep<TEntity>>, IReleaseToQueuePreStep<TEntity>,
    IModeSelector<IReleaseToQueuePostStep<TEntity>>, IReleaseToQueuePostStep<TEntity>,
    ISendEmailStepStage<TEntity>,
    IModeSelector<ISendEmailPreStep<TEntity>>, ISendEmailPreStep<TEntity>,
    IModeSelector<ISendEmailPostStep<TEntity>>, ISendEmailPostStep<TEntity>,
    ISendEmailFromTemplateStepStage<TEntity>,
    IModeSelector<ISendEmailFromTemplatePreStep<TEntity>>, ISendEmailFromTemplatePreStep<TEntity>,
    IModeSelector<ISendEmailFromTemplatePostStep<TEntity>>, ISendEmailFromTemplatePostStep<TEntity>,
    IDeliverIncomingEmailStepStage<TEntity>,
    IModeSelector<IDeliverIncomingEmailPreStep<TEntity>>, IDeliverIncomingEmailPreStep<TEntity>,
    IModeSelector<IDeliverIncomingEmailPostStep<TEntity>>, IDeliverIncomingEmailPostStep<TEntity>,
    IDeliverPromoteEmailStepStage<TEntity>,
    IModeSelector<IDeliverPromoteEmailPreStep<TEntity>>, IDeliverPromoteEmailPreStep<TEntity>,
    IModeSelector<IDeliverPromoteEmailPostStep<TEntity>>, IDeliverPromoteEmailPostStep<TEntity>,
    IAddMembersTeamStepStage<TEntity>,
    IModeSelector<IAddMembersTeamPreStep<TEntity>>, IAddMembersTeamPreStep<TEntity>,
    IModeSelector<IAddMembersTeamPostStep<TEntity>>, IAddMembersTeamPostStep<TEntity>,
    IRemoveMembersTeamStepStage<TEntity>,
    IModeSelector<IRemoveMembersTeamPreStep<TEntity>>, IRemoveMembersTeamPreStep<TEntity>,
    IModeSelector<IRemoveMembersTeamPostStep<TEntity>>, IRemoveMembersTeamPostStep<TEntity>,
    IAddListMembersListStepStage<TEntity>,
    IModeSelector<IAddListMembersListPreStep<TEntity>>, IAddListMembersListPreStep<TEntity>,
    IModeSelector<IAddListMembersListPostStep<TEntity>>, IAddListMembersListPostStep<TEntity>,
    IAddMemberListStepStage<TEntity>,
    IModeSelector<IAddMemberListPreStep<TEntity>>, IAddMemberListPreStep<TEntity>,
    IModeSelector<IAddMemberListPostStep<TEntity>>, IAddMemberListPostStep<TEntity>,
    IRemoveMemberListStepStage<TEntity>,
    IModeSelector<IRemoveMemberListPreStep<TEntity>>, IRemoveMemberListPreStep<TEntity>,
    IModeSelector<IRemoveMemberListPostStep<TEntity>>, IRemoveMemberListPostStep<TEntity>,
    ICloseIncidentStepStage<TEntity>,
    IModeSelector<ICloseIncidentPreStep<TEntity>>, ICloseIncidentPreStep<TEntity>,
    IModeSelector<ICloseIncidentPostStep<TEntity>>, ICloseIncidentPostStep<TEntity>,
    ICancelContractStepStage<TEntity>,
    IModeSelector<ICancelContractPreStep<TEntity>>, ICancelContractPreStep<TEntity>,
    IModeSelector<ICancelContractPostStep<TEntity>>, ICancelContractPostStep<TEntity>,
    ICloneContractStepStage<TEntity>,
    IModeSelector<ICloneContractPreStep<TEntity>>, ICloneContractPreStep<TEntity>,
    IModeSelector<ICloneContractPostStep<TEntity>>, ICloneContractPostStep<TEntity>,
    IRetrievePrincipalAccessStepStage<TEntity>,
    IModeSelector<IRetrievePrincipalAccessPreStep<TEntity>>, IRetrievePrincipalAccessPreStep<TEntity>,
    IModeSelector<IRetrievePrincipalAccessPostStep<TEntity>>, IRetrievePrincipalAccessPostStep<TEntity>,
    IRetrieveSharedPrincipalsAndAccessStepStage<TEntity>,
    IModeSelector<IRetrieveSharedPrincipalsAndAccessPreStep<TEntity>>, IRetrieveSharedPrincipalsAndAccessPreStep<TEntity>,
    IModeSelector<IRetrieveSharedPrincipalsAndAccessPostStep<TEntity>>, IRetrieveSharedPrincipalsAndAccessPostStep<TEntity>,
    IWhoAmIStepStage<TEntity>,
    IModeSelector<IWhoAmIPreStep<TEntity>>, IWhoAmIPreStep<TEntity>,
    IModeSelector<IWhoAmIPostStep<TEntity>>, IWhoAmIPostStep<TEntity>,
    IWinQuoteStepStage<TEntity>,
    IModeSelector<IWinQuotePreStep<TEntity>>, IWinQuotePreStep<TEntity>,
    IModeSelector<IWinQuotePostStep<TEntity>>, IWinQuotePostStep<TEntity>,
    IReviseQuoteStepStage<TEntity>,
    IModeSelector<IReviseQuotePreStep<TEntity>>, IReviseQuotePreStep<TEntity>,
    IModeSelector<IReviseQuotePostStep<TEntity>>, IReviseQuotePostStep<TEntity>,
    IFulfillSalesOrderStepStage<TEntity>,
    IModeSelector<IFulfillSalesOrderPreStep<TEntity>>, IFulfillSalesOrderPreStep<TEntity>,
    IModeSelector<IFulfillSalesOrderPostStep<TEntity>>, IFulfillSalesOrderPostStep<TEntity>,
    IGetQuoteProductsFromOpportunityStepStage<TEntity>,
    IModeSelector<IGetQuoteProductsFromOpportunityPreStep<TEntity>>, IGetQuoteProductsFromOpportunityPreStep<TEntity>,
    IModeSelector<IGetQuoteProductsFromOpportunityPostStep<TEntity>>, IGetQuoteProductsFromOpportunityPostStep<TEntity>,
    IGetSalesOrderProductsFromOpportunityStepStage<TEntity>,
    IModeSelector<IGetSalesOrderProductsFromOpportunityPreStep<TEntity>>, IGetSalesOrderProductsFromOpportunityPreStep<TEntity>,
    IModeSelector<IGetSalesOrderProductsFromOpportunityPostStep<TEntity>>, IGetSalesOrderProductsFromOpportunityPostStep<TEntity>,
    IGetInvoiceProductsFromOpportunityStepStage<TEntity>,
    IModeSelector<IGetInvoiceProductsFromOpportunityPreStep<TEntity>>, IGetInvoiceProductsFromOpportunityPreStep<TEntity>,
    IModeSelector<IGetInvoiceProductsFromOpportunityPostStep<TEntity>>, IGetInvoiceProductsFromOpportunityPostStep<TEntity>,
    IModeSelector<IFilteringSelector<TEntity, INoImageStep<TEntity>>>,
    IModeSelector<IFilteringSelector<TEntity, IPreImageStep<TEntity>>>,
    IModeSelector<IFilteringSelector<TEntity, IPostImageStep<TEntity>>>,
    IModeSelector<IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>>>,
    IModeSelector<INoImageStep<TEntity>>,
    IModeSelector<IPreImageStep<TEntity>>,
    IModeSelector<IPreAndPostImageStep<TEntity>>,
    IFilteringSelector<TEntity, INoImageStep<TEntity>>,
    IFilteringSelector<TEntity, IPreImageStep<TEntity>>,
    IFilteringSelector<TEntity, IPostImageStep<TEntity>>,
    IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>>,
    INoImageStep<TEntity>,
    IPreImageConfig<TEntity>,
    IPostImageConfig<TEntity>,
    IPreAndPostImageConfig<TEntity>
    where TEntity : Entity
{
    private readonly StepDefinition _definition = new();
    private readonly List<ImageDefinition> _preImages = new();
    private readonly List<ImageDefinition> _postImages = new();
    private ImageDefinition? _currentImage;
    private bool _isPreImage;

    /// <summary>
    /// Creates a new step builder for the specified entity type.
    /// </summary>
    public StepBuilder()
    {
        // Get entity logical name from EntityLogicalNameAttribute
        var attribute = typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>();
        _definition.EntityLogicalName = attribute?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant();
    }

    // ---- Message entries -----
    public ICreateStepStage<TEntity> OnCreate() { _definition.Message = PluginMessages.Create; return this; }
    public IUpdateStepStage<TEntity> OnUpdate() { _definition.Message = PluginMessages.Update; return this; }
    public IDeleteStepStage<TEntity> OnDelete() { _definition.Message = PluginMessages.Delete; return this; }
    public ICreateMultipleStepStage<TEntity> OnCreateMultiple() { _definition.Message = PluginMessages.CreateMultiple; return this; }
    public IUpdateMultipleStepStage<TEntity> OnUpdateMultiple() { _definition.Message = PluginMessages.UpdateMultiple; return this; }
    public IRetrieveStepStage<TEntity> OnRetrieve() { _definition.Message = PluginMessages.Retrieve; return this; }
    public IRetrieveMultipleStepStage<TEntity> OnRetrieveMultiple() { _definition.Message = PluginMessages.RetrieveMultiple; return this; }
    public IAssociateStepStage<TEntity> OnAssociate() { _definition.Message = PluginMessages.Associate; return this; }
    public IDisassociateStepStage<TEntity> OnDisassociate() { _definition.Message = PluginMessages.Disassociate; return this; }
    public IMergeStepStage<TEntity> OnMerge() { _definition.Message = PluginMessages.Merge; return this; }
    public IGrantAccessStepStage<TEntity> OnGrantAccess() { _definition.Message = PluginMessages.GrantAccess; return this; }
    public IModifyAccessStepStage<TEntity> OnModifyAccess() { _definition.Message = PluginMessages.ModifyAccess; return this; }
    public IRevokeAccessStepStage<TEntity> OnRevokeAccess() { _definition.Message = PluginMessages.RevokeAccess; return this; }
    public IQualifyLeadStepStage<TEntity> OnQualifyLead() { _definition.Message = PluginMessages.QualifyLead; return this; }
    public IWinOpportunityStepStage<TEntity> OnWinOpportunity() { _definition.Message = PluginMessages.WinOpportunity; return this; }
    public ILoseOpportunityStepStage<TEntity> OnLoseOpportunity() { _definition.Message = PluginMessages.LoseOpportunity; return this; }
    public IConvertQuoteToSalesOrderStepStage<TEntity> OnConvertQuoteToSalesOrder() { _definition.Message = PluginMessages.ConvertQuoteToSalesOrder; return this; }
    public IConvertSalesOrderToInvoiceStepStage<TEntity> OnConvertSalesOrderToInvoice() { _definition.Message = PluginMessages.ConvertSalesOrderToInvoice; return this; }
    public IGenerateQuoteFromOpportunityStepStage<TEntity> OnGenerateQuoteFromOpportunity() { _definition.Message = PluginMessages.GenerateQuoteFromOpportunity; return this; }
    public IGenerateSalesOrderFromOpportunityStepStage<TEntity> OnGenerateSalesOrderFromOpportunity() { _definition.Message = PluginMessages.GenerateSalesOrderFromOpportunity; return this; }
    public IGenerateInvoiceFromOpportunityStepStage<TEntity> OnGenerateInvoiceFromOpportunity() { _definition.Message = PluginMessages.GenerateInvoiceFromOpportunity; return this; }
    public IAddToQueueStepStage<TEntity> OnAddToQueue() { _definition.Message = PluginMessages.AddToQueue; return this; }
    public IRemoveFromQueueStepStage<TEntity> OnRemoveFromQueue() { _definition.Message = PluginMessages.RemoveFromQueue; return this; }
    public IPickFromQueueStepStage<TEntity> OnPickFromQueue() { _definition.Message = PluginMessages.PickFromQueue; return this; }
    public IRouteToStepStage<TEntity> OnRouteTo() { _definition.Message = PluginMessages.RouteTo; return this; }
    public IReleaseToQueueStepStage<TEntity> OnReleaseToQueue() { _definition.Message = PluginMessages.ReleaseToQueue; return this; }
    public ISendEmailStepStage<TEntity> OnSendEmail() { _definition.Message = PluginMessages.SendEmail; return this; }
    public ISendEmailFromTemplateStepStage<TEntity> OnSendEmailFromTemplate() { _definition.Message = PluginMessages.SendEmailFromTemplate; return this; }
    public IDeliverIncomingEmailStepStage<TEntity> OnDeliverIncomingEmail() { _definition.Message = PluginMessages.DeliverIncomingEmail; return this; }
    public IDeliverPromoteEmailStepStage<TEntity> OnDeliverPromoteEmail() { _definition.Message = PluginMessages.DeliverPromoteEmail; return this; }
    public IAddMembersTeamStepStage<TEntity> OnAddMembersTeam() { _definition.Message = PluginMessages.AddMembersTeam; return this; }
    public IRemoveMembersTeamStepStage<TEntity> OnRemoveMembersTeam() { _definition.Message = PluginMessages.RemoveMembersTeam; return this; }
    public IAddListMembersListStepStage<TEntity> OnAddListMembersList() { _definition.Message = PluginMessages.AddListMembersList; return this; }
    public IAddMemberListStepStage<TEntity> OnAddMemberList() { _definition.Message = PluginMessages.AddMemberList; return this; }
    public IRemoveMemberListStepStage<TEntity> OnRemoveMemberList() { _definition.Message = PluginMessages.RemoveMemberList; return this; }
    public ICloseIncidentStepStage<TEntity> OnCloseIncident() { _definition.Message = PluginMessages.CloseIncident; return this; }
    public ICancelContractStepStage<TEntity> OnCancelContract() { _definition.Message = PluginMessages.CancelContract; return this; }
    public ICloneContractStepStage<TEntity> OnCloneContract() { _definition.Message = PluginMessages.CloneContract; return this; }
    public IRetrievePrincipalAccessStepStage<TEntity> OnRetrievePrincipalAccess() { _definition.Message = PluginMessages.RetrievePrincipalAccess; return this; }
    public IRetrieveSharedPrincipalsAndAccessStepStage<TEntity> OnRetrieveSharedPrincipalsAndAccess() { _definition.Message = PluginMessages.RetrieveSharedPrincipalsAndAccess; return this; }
    public IWhoAmIStepStage<TEntity> OnWhoAmI() { _definition.Message = PluginMessages.WhoAmI; return this; }
    public IWinQuoteStepStage<TEntity> OnWinQuote() { _definition.Message = PluginMessages.WinQuote; return this; }
    public IReviseQuoteStepStage<TEntity> OnReviseQuote() { _definition.Message = PluginMessages.ReviseQuote; return this; }
    public IFulfillSalesOrderStepStage<TEntity> OnFulfillSalesOrder() { _definition.Message = PluginMessages.FulfillSalesOrder; return this; }
    public IGetQuoteProductsFromOpportunityStepStage<TEntity> OnGetQuoteProductsFromOpportunity() { _definition.Message = PluginMessages.GetQuoteProductsFromOpportunity; return this; }
    public IGetSalesOrderProductsFromOpportunityStepStage<TEntity> OnGetSalesOrderProductsFromOpportunity() { _definition.Message = PluginMessages.GetSalesOrderProductsFromOpportunity; return this; }
    public IGetInvoiceProductsFromOpportunityStepStage<TEntity> OnGetInvoiceProductsFromOpportunity() { _definition.Message = PluginMessages.GetInvoiceProductsFromOpportunity; return this; }

    public IPermissiveStepStage<TEntity> OnMessage(string messageName)
    {
        if (string.IsNullOrWhiteSpace(messageName))
        {
            throw new ArgumentNullException(nameof(messageName));
        }
        _definition.Message = messageName;
        return this;
    }

    public IPermissiveStepStage<TEntity> OnMessage<TMessage>() where TMessage : class
    {
        _definition.Message = ExpressionHelper.GetMessageName<TMessage>();
        return this;
    }

    public ITypedMessageStepStage<TEntity, TRequest> On<TRequest>() where TRequest : OrganizationRequest, new()
    {
        // The message name comes from the request type itself (its RequestName), so the type is stated once and
        // no magic string is needed. The returned adapter carries TRequest to the handler (its ctx.Request is
        // IMessagePre/PostContext<TRequest>) while the mutable step state stays on this builder.
        _definition.Message = ExpressionHelper.GetMessageName<TRequest>();
        return new TypedMessageStep<TEntity, TRequest>(this);
    }

    IModeSelector<IPermissiveNoImageStep<TEntity>> IPermissiveStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IPermissivePreImageStep<TEntity>> IPermissiveStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IPermissivePreAndPostStep<TEntity>> IPermissiveStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }

    IPermissiveNoImageStep<TEntity> IModeSelector<IPermissiveNoImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IPermissivePreImageStep<TEntity> IModeSelector<IPermissivePreImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IPermissivePreAndPostStep<TEntity> IModeSelector<IPermissivePreAndPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }

    INoImageStep<TEntity> IPermissiveNoImageStep<TEntity>.Handler(Action<ILocalPluginContext> handler) { SetHandler(handler); return this; }
    IPreImageStep<TEntity> IPermissivePreImageStep<TEntity>.Handler(Action<ILocalPluginContext> handler) { SetHandler(handler); return this; }
    IPreAndPostImageStep<TEntity> IPermissivePreAndPostStep<TEntity>.Handler(Action<ILocalPluginContext> handler) { SetHandler(handler); return this; }
    INoImageStep<TEntity> IPermissiveNoImageStep<TEntity>.Handler<TRequest>(Action<IMessagePreContext<TRequest>> handler) { SetHandler(handler); return this; }
    IPreImageStep<TEntity> IPermissivePreImageStep<TEntity>.Handler<TRequest>(Action<IMessagePreContext<TRequest>> handler) { SetHandler(handler); return this; }
    IPreAndPostImageStep<TEntity> IPermissivePreAndPostStep<TEntity>.Handler<TRequest>(Action<IMessagePostContext<TRequest>> handler) { SetHandler(handler); return this; }
    // The entity-typed permissive overloads name their target-entity type parameter TEntity to match the public
    // surface, which shadows the builder's own TEntity. The Target is always the plugin's own entity, so the two
    // coincide at every real call site and returning this back through the shadowed terminal type is safe.
    INoImageStep<TEntity> IPermissiveNoImageStep<TEntity>.Handler<TRequest>(Action<IMessagePreContext<TRequest, TEntity>> handler) { SetHandler(handler); return this; }
    IPreImageStep<TEntity> IPermissivePreImageStep<TEntity>.Handler<TRequest>(Action<IMessagePreContext<TRequest, TEntity>> handler) { SetHandler(handler); return this; }
    IPreAndPostImageStep<TEntity> IPermissivePreAndPostStep<TEntity>.Handler<TRequest>(Action<IMessagePostContext<TRequest, TEntity>> handler) { SetHandler(handler); return this; }

    // ---- Per-message stage selectors / mode / filtering / handler -----
    IModeSelector<IFilteringSelector<TEntity, ICreatePreStep<TEntity>>> ICreateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, ICreatePreStep<TEntity>>> ICreateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, ICreatePostStep<TEntity>>> ICreateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IFilteringSelector<TEntity, ICreatePreStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, ICreatePreStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    ICreatePreStep<TEntity> IFilteringSelector<TEntity, ICreatePreStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    INoImageStep<TEntity> ICreatePreStep<TEntity>.Handler(Action<ICreatePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IFilteringSelector<TEntity, ICreatePostStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, ICreatePostStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    ICreatePostStep<TEntity> IFilteringSelector<TEntity, ICreatePostStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IPostImageStep<TEntity> ICreatePostStep<TEntity>.Handler(Action<ICreatePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreValStep<TEntity>>> IUpdateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreOpStep<TEntity>>> IUpdateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdatePostStep<TEntity>>> IUpdateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IFilteringSelector<TEntity, IUpdatePreValStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdatePreValStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IUpdatePreValStep<TEntity> IFilteringSelector<TEntity, IUpdatePreValStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    INoImageStep<TEntity> IUpdatePreValStep<TEntity>.Handler(Action<IUpdatePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IFilteringSelector<TEntity, IUpdatePreOpStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdatePreOpStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IUpdatePreOpStep<TEntity> IFilteringSelector<TEntity, IUpdatePreOpStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IPreImageStep<TEntity> IUpdatePreOpStep<TEntity>.Handler(Action<IUpdatePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IFilteringSelector<TEntity, IUpdatePostStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdatePostStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IUpdatePostStep<TEntity> IFilteringSelector<TEntity, IUpdatePostStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IPreAndPostImageStep<TEntity> IUpdatePostStep<TEntity>.Handler(Action<IUpdatePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IDeletePreValStep<TEntity>> IDeleteStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IDeletePreOpStep<TEntity>> IDeleteStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IDeletePostStep<TEntity>> IDeleteStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IDeletePreValStep<TEntity> IModeSelector<IDeletePreValStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IDeletePreValStep<TEntity>.Handler(Action<IDeletePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IDeletePreOpStep<TEntity> IModeSelector<IDeletePreOpStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IPreImageStep<TEntity> IDeletePreOpStep<TEntity>.Handler(Action<IDeletePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IDeletePostStep<TEntity> IModeSelector<IDeletePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IPreImageStep<TEntity> IDeletePostStep<TEntity>.Handler(Action<IDeletePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePreStep<TEntity>>> ICreateMultipleStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePreStep<TEntity>>> ICreateMultipleStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePostStep<TEntity>>> ICreateMultipleStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IFilteringSelector<TEntity, ICreateMultiplePreStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePreStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    ICreateMultiplePreStep<TEntity> IFilteringSelector<TEntity, ICreateMultiplePreStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    INoImageStep<TEntity> ICreateMultiplePreStep<TEntity>.Handler(Action<ICreateMultiplePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IFilteringSelector<TEntity, ICreateMultiplePostStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePostStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    ICreateMultiplePostStep<TEntity> IFilteringSelector<TEntity, ICreateMultiplePostStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IPostImageStep<TEntity> ICreateMultiplePostStep<TEntity>.Handler(Action<ICreateMultiplePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreValStep<TEntity>>> IUpdateMultipleStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreOpStep<TEntity>>> IUpdateMultipleStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePostStep<TEntity>>> IUpdateMultipleStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IFilteringSelector<TEntity, IUpdateMultiplePreValStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreValStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IUpdateMultiplePreValStep<TEntity> IFilteringSelector<TEntity, IUpdateMultiplePreValStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    INoImageStep<TEntity> IUpdateMultiplePreValStep<TEntity>.Handler(Action<IUpdateMultiplePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IFilteringSelector<TEntity, IUpdateMultiplePreOpStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreOpStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IUpdateMultiplePreOpStep<TEntity> IFilteringSelector<TEntity, IUpdateMultiplePreOpStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IPreImageStep<TEntity> IUpdateMultiplePreOpStep<TEntity>.Handler(Action<IUpdateMultiplePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IFilteringSelector<TEntity, IUpdateMultiplePostStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePostStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IUpdateMultiplePostStep<TEntity> IFilteringSelector<TEntity, IUpdateMultiplePostStep<TEntity>>.FilteringAttributes(System.Linq.Expressions.Expression<Func<TEntity, object?>> attribute, params System.Linq.Expressions.Expression<Func<TEntity, object?>>[] additionalAttributes) { SetFilteringAttributes(attribute, additionalAttributes); return this; }
    IPreAndPostImageStep<TEntity> IUpdateMultiplePostStep<TEntity>.Handler(Action<IUpdateMultiplePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IRetrievePreStep<TEntity>> IRetrieveStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRetrievePreStep<TEntity>> IRetrieveStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRetrievePostStep<TEntity>> IRetrieveStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IRetrievePreStep<TEntity> IModeSelector<IRetrievePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRetrievePreStep<TEntity>.Handler(Action<IRetrievePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IRetrievePostStep<TEntity> IModeSelector<IRetrievePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRetrievePostStep<TEntity>.Handler(Action<IRetrievePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IRetrieveMultiplePreStep<TEntity>> IRetrieveMultipleStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRetrieveMultiplePreStep<TEntity>> IRetrieveMultipleStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRetrieveMultiplePostStep<TEntity>> IRetrieveMultipleStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IRetrieveMultiplePreStep<TEntity> IModeSelector<IRetrieveMultiplePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRetrieveMultiplePreStep<TEntity>.Handler(Action<IRetrieveMultiplePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IRetrieveMultiplePostStep<TEntity> IModeSelector<IRetrieveMultiplePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRetrieveMultiplePostStep<TEntity>.Handler(Action<IRetrieveMultiplePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IAssociatePreStep<TEntity>> IAssociateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IAssociatePreStep<TEntity>> IAssociateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IAssociatePostStep<TEntity>> IAssociateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IAssociatePreStep<TEntity> IModeSelector<IAssociatePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAssociatePreStep<TEntity>.Handler(Action<IAssociatePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IAssociatePostStep<TEntity> IModeSelector<IAssociatePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAssociatePostStep<TEntity>.Handler(Action<IAssociatePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IDisassociatePreStep<TEntity>> IDisassociateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IDisassociatePreStep<TEntity>> IDisassociateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IDisassociatePostStep<TEntity>> IDisassociateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IDisassociatePreStep<TEntity> IModeSelector<IDisassociatePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IDisassociatePreStep<TEntity>.Handler(Action<IDisassociatePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IDisassociatePostStep<TEntity> IModeSelector<IDisassociatePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IDisassociatePostStep<TEntity>.Handler(Action<IDisassociatePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IMergePreStep<TEntity>> IMergeStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IMergePreStep<TEntity>> IMergeStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IMergePostStep<TEntity>> IMergeStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IMergePreStep<TEntity> IModeSelector<IMergePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IMergePreStep<TEntity>.Handler(Action<IMergePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IMergePostStep<TEntity> IModeSelector<IMergePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IMergePostStep<TEntity>.Handler(Action<IMergePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IGrantAccessPreStep<TEntity>> IGrantAccessStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IGrantAccessPreStep<TEntity>> IGrantAccessStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IGrantAccessPostStep<TEntity>> IGrantAccessStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IGrantAccessPreStep<TEntity> IModeSelector<IGrantAccessPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGrantAccessPreStep<TEntity>.Handler(Action<IGrantAccessPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IGrantAccessPostStep<TEntity> IModeSelector<IGrantAccessPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGrantAccessPostStep<TEntity>.Handler(Action<IGrantAccessPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IModifyAccessPreStep<TEntity>> IModifyAccessStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IModifyAccessPreStep<TEntity>> IModifyAccessStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IModifyAccessPostStep<TEntity>> IModifyAccessStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IModifyAccessPreStep<TEntity> IModeSelector<IModifyAccessPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IModifyAccessPreStep<TEntity>.Handler(Action<IModifyAccessPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModifyAccessPostStep<TEntity> IModeSelector<IModifyAccessPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IModifyAccessPostStep<TEntity>.Handler(Action<IModifyAccessPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IRevokeAccessPreStep<TEntity>> IRevokeAccessStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRevokeAccessPreStep<TEntity>> IRevokeAccessStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRevokeAccessPostStep<TEntity>> IRevokeAccessStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IRevokeAccessPreStep<TEntity> IModeSelector<IRevokeAccessPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRevokeAccessPreStep<TEntity>.Handler(Action<IRevokeAccessPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IRevokeAccessPostStep<TEntity> IModeSelector<IRevokeAccessPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRevokeAccessPostStep<TEntity>.Handler(Action<IRevokeAccessPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IQualifyLeadPreStep<TEntity>> IQualifyLeadStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IQualifyLeadPreStep<TEntity>> IQualifyLeadStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IQualifyLeadPostStep<TEntity>> IQualifyLeadStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IQualifyLeadPreStep<TEntity> IModeSelector<IQualifyLeadPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IQualifyLeadPreStep<TEntity>.Handler(Action<IQualifyLeadPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IQualifyLeadPostStep<TEntity> IModeSelector<IQualifyLeadPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IQualifyLeadPostStep<TEntity>.Handler(Action<IQualifyLeadPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IWinOpportunityPreStep<TEntity>> IWinOpportunityStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IWinOpportunityPreStep<TEntity>> IWinOpportunityStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IWinOpportunityPostStep<TEntity>> IWinOpportunityStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IWinOpportunityPreStep<TEntity> IModeSelector<IWinOpportunityPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IWinOpportunityPreStep<TEntity>.Handler(Action<IWinOpportunityPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IWinOpportunityPostStep<TEntity> IModeSelector<IWinOpportunityPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IWinOpportunityPostStep<TEntity>.Handler(Action<IWinOpportunityPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<ILoseOpportunityPreStep<TEntity>> ILoseOpportunityStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<ILoseOpportunityPreStep<TEntity>> ILoseOpportunityStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<ILoseOpportunityPostStep<TEntity>> ILoseOpportunityStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    ILoseOpportunityPreStep<TEntity> IModeSelector<ILoseOpportunityPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ILoseOpportunityPreStep<TEntity>.Handler(Action<ILoseOpportunityPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    ILoseOpportunityPostStep<TEntity> IModeSelector<ILoseOpportunityPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ILoseOpportunityPostStep<TEntity>.Handler(Action<ILoseOpportunityPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IConvertQuoteToSalesOrderPreStep<TEntity>> IConvertQuoteToSalesOrderStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IConvertQuoteToSalesOrderPreStep<TEntity>> IConvertQuoteToSalesOrderStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IConvertQuoteToSalesOrderPostStep<TEntity>> IConvertQuoteToSalesOrderStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IConvertQuoteToSalesOrderPreStep<TEntity> IModeSelector<IConvertQuoteToSalesOrderPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IConvertQuoteToSalesOrderPreStep<TEntity>.Handler(Action<IConvertQuoteToSalesOrderPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IConvertQuoteToSalesOrderPostStep<TEntity> IModeSelector<IConvertQuoteToSalesOrderPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IConvertQuoteToSalesOrderPostStep<TEntity>.Handler(Action<IConvertQuoteToSalesOrderPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IConvertSalesOrderToInvoicePreStep<TEntity>> IConvertSalesOrderToInvoiceStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IConvertSalesOrderToInvoicePreStep<TEntity>> IConvertSalesOrderToInvoiceStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IConvertSalesOrderToInvoicePostStep<TEntity>> IConvertSalesOrderToInvoiceStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IConvertSalesOrderToInvoicePreStep<TEntity> IModeSelector<IConvertSalesOrderToInvoicePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IConvertSalesOrderToInvoicePreStep<TEntity>.Handler(Action<IConvertSalesOrderToInvoicePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IConvertSalesOrderToInvoicePostStep<TEntity> IModeSelector<IConvertSalesOrderToInvoicePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IConvertSalesOrderToInvoicePostStep<TEntity>.Handler(Action<IConvertSalesOrderToInvoicePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IGenerateQuoteFromOpportunityPreStep<TEntity>> IGenerateQuoteFromOpportunityStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IGenerateQuoteFromOpportunityPreStep<TEntity>> IGenerateQuoteFromOpportunityStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IGenerateQuoteFromOpportunityPostStep<TEntity>> IGenerateQuoteFromOpportunityStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IGenerateQuoteFromOpportunityPreStep<TEntity> IModeSelector<IGenerateQuoteFromOpportunityPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGenerateQuoteFromOpportunityPreStep<TEntity>.Handler(Action<IGenerateQuoteFromOpportunityPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IGenerateQuoteFromOpportunityPostStep<TEntity> IModeSelector<IGenerateQuoteFromOpportunityPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGenerateQuoteFromOpportunityPostStep<TEntity>.Handler(Action<IGenerateQuoteFromOpportunityPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IGenerateSalesOrderFromOpportunityPreStep<TEntity>> IGenerateSalesOrderFromOpportunityStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IGenerateSalesOrderFromOpportunityPreStep<TEntity>> IGenerateSalesOrderFromOpportunityStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IGenerateSalesOrderFromOpportunityPostStep<TEntity>> IGenerateSalesOrderFromOpportunityStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IGenerateSalesOrderFromOpportunityPreStep<TEntity> IModeSelector<IGenerateSalesOrderFromOpportunityPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGenerateSalesOrderFromOpportunityPreStep<TEntity>.Handler(Action<IGenerateSalesOrderFromOpportunityPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IGenerateSalesOrderFromOpportunityPostStep<TEntity> IModeSelector<IGenerateSalesOrderFromOpportunityPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGenerateSalesOrderFromOpportunityPostStep<TEntity>.Handler(Action<IGenerateSalesOrderFromOpportunityPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IGenerateInvoiceFromOpportunityPreStep<TEntity>> IGenerateInvoiceFromOpportunityStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IGenerateInvoiceFromOpportunityPreStep<TEntity>> IGenerateInvoiceFromOpportunityStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IGenerateInvoiceFromOpportunityPostStep<TEntity>> IGenerateInvoiceFromOpportunityStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IGenerateInvoiceFromOpportunityPreStep<TEntity> IModeSelector<IGenerateInvoiceFromOpportunityPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGenerateInvoiceFromOpportunityPreStep<TEntity>.Handler(Action<IGenerateInvoiceFromOpportunityPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IGenerateInvoiceFromOpportunityPostStep<TEntity> IModeSelector<IGenerateInvoiceFromOpportunityPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGenerateInvoiceFromOpportunityPostStep<TEntity>.Handler(Action<IGenerateInvoiceFromOpportunityPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IAddToQueuePreStep<TEntity>> IAddToQueueStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IAddToQueuePreStep<TEntity>> IAddToQueueStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IAddToQueuePostStep<TEntity>> IAddToQueueStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IAddToQueuePreStep<TEntity> IModeSelector<IAddToQueuePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAddToQueuePreStep<TEntity>.Handler(Action<IAddToQueuePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IAddToQueuePostStep<TEntity> IModeSelector<IAddToQueuePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAddToQueuePostStep<TEntity>.Handler(Action<IAddToQueuePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IRemoveFromQueuePreStep<TEntity>> IRemoveFromQueueStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRemoveFromQueuePreStep<TEntity>> IRemoveFromQueueStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRemoveFromQueuePostStep<TEntity>> IRemoveFromQueueStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IRemoveFromQueuePreStep<TEntity> IModeSelector<IRemoveFromQueuePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRemoveFromQueuePreStep<TEntity>.Handler(Action<IRemoveFromQueuePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IRemoveFromQueuePostStep<TEntity> IModeSelector<IRemoveFromQueuePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRemoveFromQueuePostStep<TEntity>.Handler(Action<IRemoveFromQueuePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IPickFromQueuePreStep<TEntity>> IPickFromQueueStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IPickFromQueuePreStep<TEntity>> IPickFromQueueStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IPickFromQueuePostStep<TEntity>> IPickFromQueueStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IPickFromQueuePreStep<TEntity> IModeSelector<IPickFromQueuePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IPickFromQueuePreStep<TEntity>.Handler(Action<IPickFromQueuePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IPickFromQueuePostStep<TEntity> IModeSelector<IPickFromQueuePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IPickFromQueuePostStep<TEntity>.Handler(Action<IPickFromQueuePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IRouteToPreStep<TEntity>> IRouteToStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRouteToPreStep<TEntity>> IRouteToStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRouteToPostStep<TEntity>> IRouteToStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IRouteToPreStep<TEntity> IModeSelector<IRouteToPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRouteToPreStep<TEntity>.Handler(Action<IRouteToPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IRouteToPostStep<TEntity> IModeSelector<IRouteToPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRouteToPostStep<TEntity>.Handler(Action<IRouteToPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IReleaseToQueuePreStep<TEntity>> IReleaseToQueueStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IReleaseToQueuePreStep<TEntity>> IReleaseToQueueStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IReleaseToQueuePostStep<TEntity>> IReleaseToQueueStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IReleaseToQueuePreStep<TEntity> IModeSelector<IReleaseToQueuePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IReleaseToQueuePreStep<TEntity>.Handler(Action<IReleaseToQueuePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IReleaseToQueuePostStep<TEntity> IModeSelector<IReleaseToQueuePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IReleaseToQueuePostStep<TEntity>.Handler(Action<IReleaseToQueuePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<ISendEmailPreStep<TEntity>> ISendEmailStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<ISendEmailPreStep<TEntity>> ISendEmailStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<ISendEmailPostStep<TEntity>> ISendEmailStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    ISendEmailPreStep<TEntity> IModeSelector<ISendEmailPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ISendEmailPreStep<TEntity>.Handler(Action<ISendEmailPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    ISendEmailPostStep<TEntity> IModeSelector<ISendEmailPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ISendEmailPostStep<TEntity>.Handler(Action<ISendEmailPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<ISendEmailFromTemplatePreStep<TEntity>> ISendEmailFromTemplateStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<ISendEmailFromTemplatePreStep<TEntity>> ISendEmailFromTemplateStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<ISendEmailFromTemplatePostStep<TEntity>> ISendEmailFromTemplateStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    ISendEmailFromTemplatePreStep<TEntity> IModeSelector<ISendEmailFromTemplatePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ISendEmailFromTemplatePreStep<TEntity>.Handler(Action<ISendEmailFromTemplatePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    ISendEmailFromTemplatePostStep<TEntity> IModeSelector<ISendEmailFromTemplatePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ISendEmailFromTemplatePostStep<TEntity>.Handler(Action<ISendEmailFromTemplatePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IDeliverIncomingEmailPreStep<TEntity>> IDeliverIncomingEmailStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IDeliverIncomingEmailPreStep<TEntity>> IDeliverIncomingEmailStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IDeliverIncomingEmailPostStep<TEntity>> IDeliverIncomingEmailStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IDeliverIncomingEmailPreStep<TEntity> IModeSelector<IDeliverIncomingEmailPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IDeliverIncomingEmailPreStep<TEntity>.Handler(Action<IDeliverIncomingEmailPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IDeliverIncomingEmailPostStep<TEntity> IModeSelector<IDeliverIncomingEmailPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IDeliverIncomingEmailPostStep<TEntity>.Handler(Action<IDeliverIncomingEmailPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IDeliverPromoteEmailPreStep<TEntity>> IDeliverPromoteEmailStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IDeliverPromoteEmailPreStep<TEntity>> IDeliverPromoteEmailStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IDeliverPromoteEmailPostStep<TEntity>> IDeliverPromoteEmailStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IDeliverPromoteEmailPreStep<TEntity> IModeSelector<IDeliverPromoteEmailPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IDeliverPromoteEmailPreStep<TEntity>.Handler(Action<IDeliverPromoteEmailPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IDeliverPromoteEmailPostStep<TEntity> IModeSelector<IDeliverPromoteEmailPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IDeliverPromoteEmailPostStep<TEntity>.Handler(Action<IDeliverPromoteEmailPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IAddMembersTeamPreStep<TEntity>> IAddMembersTeamStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IAddMembersTeamPreStep<TEntity>> IAddMembersTeamStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IAddMembersTeamPostStep<TEntity>> IAddMembersTeamStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IAddMembersTeamPreStep<TEntity> IModeSelector<IAddMembersTeamPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAddMembersTeamPreStep<TEntity>.Handler(Action<IAddMembersTeamPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IAddMembersTeamPostStep<TEntity> IModeSelector<IAddMembersTeamPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAddMembersTeamPostStep<TEntity>.Handler(Action<IAddMembersTeamPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IRemoveMembersTeamPreStep<TEntity>> IRemoveMembersTeamStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRemoveMembersTeamPreStep<TEntity>> IRemoveMembersTeamStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRemoveMembersTeamPostStep<TEntity>> IRemoveMembersTeamStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IRemoveMembersTeamPreStep<TEntity> IModeSelector<IRemoveMembersTeamPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRemoveMembersTeamPreStep<TEntity>.Handler(Action<IRemoveMembersTeamPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IRemoveMembersTeamPostStep<TEntity> IModeSelector<IRemoveMembersTeamPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRemoveMembersTeamPostStep<TEntity>.Handler(Action<IRemoveMembersTeamPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IAddListMembersListPreStep<TEntity>> IAddListMembersListStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IAddListMembersListPreStep<TEntity>> IAddListMembersListStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IAddListMembersListPostStep<TEntity>> IAddListMembersListStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IAddListMembersListPreStep<TEntity> IModeSelector<IAddListMembersListPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAddListMembersListPreStep<TEntity>.Handler(Action<IAddListMembersListPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IAddListMembersListPostStep<TEntity> IModeSelector<IAddListMembersListPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAddListMembersListPostStep<TEntity>.Handler(Action<IAddListMembersListPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IAddMemberListPreStep<TEntity>> IAddMemberListStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IAddMemberListPreStep<TEntity>> IAddMemberListStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IAddMemberListPostStep<TEntity>> IAddMemberListStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IAddMemberListPreStep<TEntity> IModeSelector<IAddMemberListPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAddMemberListPreStep<TEntity>.Handler(Action<IAddMemberListPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IAddMemberListPostStep<TEntity> IModeSelector<IAddMemberListPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IAddMemberListPostStep<TEntity>.Handler(Action<IAddMemberListPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IRemoveMemberListPreStep<TEntity>> IRemoveMemberListStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRemoveMemberListPreStep<TEntity>> IRemoveMemberListStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRemoveMemberListPostStep<TEntity>> IRemoveMemberListStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IRemoveMemberListPreStep<TEntity> IModeSelector<IRemoveMemberListPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRemoveMemberListPreStep<TEntity>.Handler(Action<IRemoveMemberListPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IRemoveMemberListPostStep<TEntity> IModeSelector<IRemoveMemberListPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRemoveMemberListPostStep<TEntity>.Handler(Action<IRemoveMemberListPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<ICloseIncidentPreStep<TEntity>> ICloseIncidentStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<ICloseIncidentPreStep<TEntity>> ICloseIncidentStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<ICloseIncidentPostStep<TEntity>> ICloseIncidentStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    ICloseIncidentPreStep<TEntity> IModeSelector<ICloseIncidentPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ICloseIncidentPreStep<TEntity>.Handler(Action<ICloseIncidentPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    ICloseIncidentPostStep<TEntity> IModeSelector<ICloseIncidentPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ICloseIncidentPostStep<TEntity>.Handler(Action<ICloseIncidentPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<ICancelContractPreStep<TEntity>> ICancelContractStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<ICancelContractPreStep<TEntity>> ICancelContractStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<ICancelContractPostStep<TEntity>> ICancelContractStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    ICancelContractPreStep<TEntity> IModeSelector<ICancelContractPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ICancelContractPreStep<TEntity>.Handler(Action<ICancelContractPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    ICancelContractPostStep<TEntity> IModeSelector<ICancelContractPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ICancelContractPostStep<TEntity>.Handler(Action<ICancelContractPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<ICloneContractPreStep<TEntity>> ICloneContractStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<ICloneContractPreStep<TEntity>> ICloneContractStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<ICloneContractPostStep<TEntity>> ICloneContractStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    ICloneContractPreStep<TEntity> IModeSelector<ICloneContractPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ICloneContractPreStep<TEntity>.Handler(Action<ICloneContractPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    ICloneContractPostStep<TEntity> IModeSelector<ICloneContractPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> ICloneContractPostStep<TEntity>.Handler(Action<ICloneContractPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IRetrievePrincipalAccessPreStep<TEntity>> IRetrievePrincipalAccessStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRetrievePrincipalAccessPreStep<TEntity>> IRetrievePrincipalAccessStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRetrievePrincipalAccessPostStep<TEntity>> IRetrievePrincipalAccessStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IRetrievePrincipalAccessPreStep<TEntity> IModeSelector<IRetrievePrincipalAccessPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRetrievePrincipalAccessPreStep<TEntity>.Handler(Action<IRetrievePrincipalAccessPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IRetrievePrincipalAccessPostStep<TEntity> IModeSelector<IRetrievePrincipalAccessPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRetrievePrincipalAccessPostStep<TEntity>.Handler(Action<IRetrievePrincipalAccessPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IRetrieveSharedPrincipalsAndAccessPreStep<TEntity>> IRetrieveSharedPrincipalsAndAccessStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IRetrieveSharedPrincipalsAndAccessPreStep<TEntity>> IRetrieveSharedPrincipalsAndAccessStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IRetrieveSharedPrincipalsAndAccessPostStep<TEntity>> IRetrieveSharedPrincipalsAndAccessStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IRetrieveSharedPrincipalsAndAccessPreStep<TEntity> IModeSelector<IRetrieveSharedPrincipalsAndAccessPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRetrieveSharedPrincipalsAndAccessPreStep<TEntity>.Handler(Action<IRetrieveSharedPrincipalsAndAccessPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IRetrieveSharedPrincipalsAndAccessPostStep<TEntity> IModeSelector<IRetrieveSharedPrincipalsAndAccessPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IRetrieveSharedPrincipalsAndAccessPostStep<TEntity>.Handler(Action<IRetrieveSharedPrincipalsAndAccessPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IWhoAmIPreStep<TEntity>> IWhoAmIStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IWhoAmIPreStep<TEntity>> IWhoAmIStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IWhoAmIPostStep<TEntity>> IWhoAmIStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IWhoAmIPreStep<TEntity> IModeSelector<IWhoAmIPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IWhoAmIPreStep<TEntity>.Handler(Action<IWhoAmIPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IWhoAmIPostStep<TEntity> IModeSelector<IWhoAmIPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IWhoAmIPostStep<TEntity>.Handler(Action<IWhoAmIPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IWinQuotePreStep<TEntity>> IWinQuoteStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IWinQuotePreStep<TEntity>> IWinQuoteStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IWinQuotePostStep<TEntity>> IWinQuoteStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IWinQuotePreStep<TEntity> IModeSelector<IWinQuotePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IWinQuotePreStep<TEntity>.Handler(Action<IWinQuotePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IWinQuotePostStep<TEntity> IModeSelector<IWinQuotePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IWinQuotePostStep<TEntity>.Handler(Action<IWinQuotePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IReviseQuotePreStep<TEntity>> IReviseQuoteStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IReviseQuotePreStep<TEntity>> IReviseQuoteStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IReviseQuotePostStep<TEntity>> IReviseQuoteStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IReviseQuotePreStep<TEntity> IModeSelector<IReviseQuotePreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IReviseQuotePreStep<TEntity>.Handler(Action<IReviseQuotePreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IReviseQuotePostStep<TEntity> IModeSelector<IReviseQuotePostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IReviseQuotePostStep<TEntity>.Handler(Action<IReviseQuotePostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IFulfillSalesOrderPreStep<TEntity>> IFulfillSalesOrderStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IFulfillSalesOrderPreStep<TEntity>> IFulfillSalesOrderStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IFulfillSalesOrderPostStep<TEntity>> IFulfillSalesOrderStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IFulfillSalesOrderPreStep<TEntity> IModeSelector<IFulfillSalesOrderPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IFulfillSalesOrderPreStep<TEntity>.Handler(Action<IFulfillSalesOrderPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IFulfillSalesOrderPostStep<TEntity> IModeSelector<IFulfillSalesOrderPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IFulfillSalesOrderPostStep<TEntity>.Handler(Action<IFulfillSalesOrderPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IGetQuoteProductsFromOpportunityPreStep<TEntity>> IGetQuoteProductsFromOpportunityStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IGetQuoteProductsFromOpportunityPreStep<TEntity>> IGetQuoteProductsFromOpportunityStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IGetQuoteProductsFromOpportunityPostStep<TEntity>> IGetQuoteProductsFromOpportunityStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IGetQuoteProductsFromOpportunityPreStep<TEntity> IModeSelector<IGetQuoteProductsFromOpportunityPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGetQuoteProductsFromOpportunityPreStep<TEntity>.Handler(Action<IGetQuoteProductsFromOpportunityPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IGetQuoteProductsFromOpportunityPostStep<TEntity> IModeSelector<IGetQuoteProductsFromOpportunityPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGetQuoteProductsFromOpportunityPostStep<TEntity>.Handler(Action<IGetQuoteProductsFromOpportunityPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IGetSalesOrderProductsFromOpportunityPreStep<TEntity>> IGetSalesOrderProductsFromOpportunityStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IGetSalesOrderProductsFromOpportunityPreStep<TEntity>> IGetSalesOrderProductsFromOpportunityStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IGetSalesOrderProductsFromOpportunityPostStep<TEntity>> IGetSalesOrderProductsFromOpportunityStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IGetSalesOrderProductsFromOpportunityPreStep<TEntity> IModeSelector<IGetSalesOrderProductsFromOpportunityPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGetSalesOrderProductsFromOpportunityPreStep<TEntity>.Handler(Action<IGetSalesOrderProductsFromOpportunityPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IGetSalesOrderProductsFromOpportunityPostStep<TEntity> IModeSelector<IGetSalesOrderProductsFromOpportunityPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGetSalesOrderProductsFromOpportunityPostStep<TEntity>.Handler(Action<IGetSalesOrderProductsFromOpportunityPostContext<TEntity>> handler) { SetHandler(handler); return this; }
    IModeSelector<IGetInvoiceProductsFromOpportunityPreStep<TEntity>> IGetInvoiceProductsFromOpportunityStepStage<TEntity>.PreValidation() { _definition.Stage = PluginStages.PreValidation; return this; }
    IModeSelector<IGetInvoiceProductsFromOpportunityPreStep<TEntity>> IGetInvoiceProductsFromOpportunityStepStage<TEntity>.PreOperation() { _definition.Stage = PluginStages.PreOperation; return this; }
    IModeSelector<IGetInvoiceProductsFromOpportunityPostStep<TEntity>> IGetInvoiceProductsFromOpportunityStepStage<TEntity>.PostOperation() { _definition.Stage = PluginStages.PostOperation; return this; }
    IGetInvoiceProductsFromOpportunityPreStep<TEntity> IModeSelector<IGetInvoiceProductsFromOpportunityPreStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGetInvoiceProductsFromOpportunityPreStep<TEntity>.Handler(Action<IGetInvoiceProductsFromOpportunityPreContext<TEntity>> handler) { SetHandler(handler); return this; }
    IGetInvoiceProductsFromOpportunityPostStep<TEntity> IModeSelector<IGetInvoiceProductsFromOpportunityPostStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IGetInvoiceProductsFromOpportunityPostStep<TEntity>.Handler(Action<IGetInvoiceProductsFromOpportunityPostContext<TEntity>> handler) { SetHandler(handler); return this; }

    // ---- Mode selectors ----------------------------------------------------------------------------

    IFilteringSelector<TEntity, INoImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, INoImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IPreImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IPreImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IPostImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IPostImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>> IModeSelector<IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    INoImageStep<TEntity> IModeSelector<INoImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IPreImageStep<TEntity> IModeSelector<IPreImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }
    IPreAndPostImageStep<TEntity> IModeSelector<IPreAndPostImageStep<TEntity>>.Mode(ExecutionMode mode) { _definition.Mode = mode; return this; }

    // ---- Filtering selectors -----------------------------------------------------------------------

    INoImageStep<TEntity> IFilteringSelector<TEntity, INoImageStep<TEntity>>.FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)
    { SetFilteringAttributes(attribute, additionalAttributes); return this; }

    IPreImageStep<TEntity> IFilteringSelector<TEntity, IPreImageStep<TEntity>>.FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)
    { SetFilteringAttributes(attribute, additionalAttributes); return this; }

    IPostImageStep<TEntity> IFilteringSelector<TEntity, IPostImageStep<TEntity>>.FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)
    { SetFilteringAttributes(attribute, additionalAttributes); return this; }

    IPreAndPostImageStep<TEntity> IFilteringSelector<TEntity, IPreAndPostImageStep<TEntity>>.FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute, params Expression<Func<TEntity, object?>>[] additionalAttributes)
    { SetFilteringAttributes(attribute, additionalAttributes); return this; }

    // ---- Image starters ----------------------------------------------------------------------------

    IPreImageConfig<TEntity> IPreImageStep<TEntity>.PreImage() { StartImage(PluginImageAliases.PreImage, isPre: true); return this; }
    IPreImageConfig<TEntity> IPreImageStep<TEntity>.PreImage(string alias) { StartImage(alias, isPre: true); return this; }
    IPreImageStep<TEntity> IPreImageStep<TEntity>.AddPreImage(params Expression<Func<TEntity, object?>>[] attributes) { AddImageNow(PluginImageAliases.PreImage, isPre: true, attributes); return this; }

    IPostImageConfig<TEntity> IPostImageStep<TEntity>.PostImage() { StartImage(PluginImageAliases.PostImage, isPre: false); return this; }
    IPostImageConfig<TEntity> IPostImageStep<TEntity>.PostImage(string alias) { StartImage(alias, isPre: false); return this; }
    IPostImageStep<TEntity> IPostImageStep<TEntity>.AddPostImage(params Expression<Func<TEntity, object?>>[] attributes) { AddImageNow(PluginImageAliases.PostImage, isPre: false, attributes); return this; }

    IPreAndPostImageConfig<TEntity> IPreAndPostImageStep<TEntity>.PreImage() { StartImage(PluginImageAliases.PreImage, isPre: true); return this; }
    IPreAndPostImageConfig<TEntity> IPreAndPostImageStep<TEntity>.PreImage(string alias) { StartImage(alias, isPre: true); return this; }
    IPreAndPostImageConfig<TEntity> IPreAndPostImageStep<TEntity>.PostImage() { StartImage(PluginImageAliases.PostImage, isPre: false); return this; }
    IPreAndPostImageConfig<TEntity> IPreAndPostImageStep<TEntity>.PostImage(string alias) { StartImage(alias, isPre: false); return this; }
    IPreAndPostImageStep<TEntity> IPreAndPostImageStep<TEntity>.AddPreImage(params Expression<Func<TEntity, object?>>[] attributes) { AddImageNow(PluginImageAliases.PreImage, isPre: true, attributes); return this; }
    IPreAndPostImageStep<TEntity> IPreAndPostImageStep<TEntity>.AddPostImage(params Expression<Func<TEntity, object?>>[] attributes) { AddImageNow(PluginImageAliases.PostImage, isPre: false, attributes); return this; }

    // ---- Image attribute configuration -------------------------------------------------------------

    IPreImageConfig<TEntity> IPreImageConfig<TEntity>.Attributes(params Expression<Func<TEntity, object?>>[] attributes) { SetCurrentImageAttributes(attributes); return this; }
    IPostImageConfig<TEntity> IPostImageConfig<TEntity>.Attributes(params Expression<Func<TEntity, object?>>[] attributes) { SetCurrentImageAttributes(attributes); return this; }
    IPreAndPostImageConfig<TEntity> IPreAndPostImageConfig<TEntity>.Attributes(params Expression<Func<TEntity, object?>>[] attributes) { SetCurrentImageAttributes(attributes); return this; }

    // ---- Settings (IStepConfig, per capability terminal) -------------------------------------------

    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.ExecutionOrder(int order) { SetExecutionOrder(order); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Description(string description) { SetDescription(description); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { SetImpersonation(source); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { SetSupportedDeployment(deployment); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.Disabled() { SetDisabled(); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.UnsecureConfig(string configuration) { SetUnsecureConfig(configuration); return this; }
    INoImageStep<TEntity> IStepConfig<TEntity, INoImageStep<TEntity>>.SecureConfig(string configuration) { SetSecureConfig(configuration); return this; }

    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.ExecutionOrder(int order) { SetExecutionOrder(order); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Description(string description) { SetDescription(description); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { SetImpersonation(source); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { SetSupportedDeployment(deployment); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.Disabled() { SetDisabled(); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.UnsecureConfig(string configuration) { SetUnsecureConfig(configuration); return this; }
    IPreImageStep<TEntity> IStepConfig<TEntity, IPreImageStep<TEntity>>.SecureConfig(string configuration) { SetSecureConfig(configuration); return this; }

    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.ExecutionOrder(int order) { SetExecutionOrder(order); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Description(string description) { SetDescription(description); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { SetImpersonation(source); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { SetSupportedDeployment(deployment); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.Disabled() { SetDisabled(); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.UnsecureConfig(string configuration) { SetUnsecureConfig(configuration); return this; }
    IPostImageStep<TEntity> IStepConfig<TEntity, IPostImageStep<TEntity>>.SecureConfig(string configuration) { SetSecureConfig(configuration); return this; }

    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.ExecutionOrder(int order) { SetExecutionOrder(order); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Key(string stepKey) { SetKey(stepKey); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Description(string description) { SetDescription(description); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.ImpersonateUsing(ImpersonationSource source) { SetImpersonation(source); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.SupportedDeployment(SupportedDeployment deployment) { SetSupportedDeployment(deployment); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.Disabled() { SetDisabled(); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.UnsecureConfig(string configuration) { SetUnsecureConfig(configuration); return this; }
    IPreAndPostImageStep<TEntity> IStepConfig<TEntity, IPreAndPostImageStep<TEntity>>.SecureConfig(string configuration) { SetSecureConfig(configuration); return this; }

    // ---- Build (shared across all terminals) -------------------------------------------------------

    /// <summary>
    /// Gets the built step definition. Runs the runtime backstop: filtering requirement, duplicate checks,
    /// and image validity against the message/stage (regardless of how the builder was assembled).
    /// </summary>
    public StepDefinition Build()
    {
        FinalizeCurrentImage();

        _definition.PreImages = _preImages.ToArray();
        _definition.PostImages = _postImages.ToArray();

        // Hard guarantee (the type-state only forces filtering when the fluent chain is consumed; the
        // RegisterSteps() pattern registers by side-effect and discards it, so the framework calls Build()).
        // Create/Update must declare at least one filtering attribute — an unfiltered step fires on every change.
        bool requiresFiltering =
            string.Equals(_definition.Message, PluginMessages.Create, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_definition.Message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_definition.Message, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_definition.Message, PluginMessages.UpdateMultiple, StringComparison.OrdinalIgnoreCase);
        if (requiresFiltering && _definition.FilteringAttributes.Length == 0)
        {
            throw new InvalidOperationException(
                $"Step on message '{_definition.Message}' must declare at least one filtering attribute via " +
                "FilteringAttributes(...) after Mode(...). An unfiltered Create/Update step fires on every " +
                "change. Use the typed OnCreate() / OnUpdate() / OnCreateMultiple() / OnUpdateMultiple() " +
                "methods (which require the filtering attribute) rather than the permissive OnMessage(\"...\").");
        }

        // Image backstop: the typed chain already prevents an invalid image at compile time, but a step
        // assembled out of order (reflection) or via the permissive OnMessage(...) escape hatch with a
        // modelled message string is still validated here.
        foreach (ImageDefinition image in _preImages)
        {
            EnsureImageAllowed(isPreImage: true, image.Alias);
        }
        foreach (ImageDefinition image in _postImages)
        {
            EnsureImageAllowed(isPreImage: false, image.Alias);
        }

        EnsureNoDuplicates(_definition.FilteringAttributes, "filtering attribute");
        foreach (ImageDefinition img in _preImages.Concat(_postImages))
        {
            EnsureNoDuplicates(img.Attributes, $"attribute in image '{img.Alias}'");
        }
        EnsureNoDuplicates(_preImages.Concat(_postImages).Select(i => i.Alias).ToArray(), "image alias");

        return _definition;
    }

    // ---- Private mutation helpers ------------------------------------------------------------------

    private void SetFilteringAttributes(
        Expression<Func<TEntity, object?>> attribute,
        Expression<Func<TEntity, object?>>[] additionalAttributes)
    {
        if (attribute == null)
        {
            throw new ArgumentNullException(nameof(attribute));
        }

        List<Expression<Func<TEntity, object?>>> all = new() { attribute };
        if (additionalAttributes != null)
        {
            all.AddRange(additionalAttributes);
        }

        _definition.FilteringAttributes = ExpressionHelper.GetAttributeNames(all.ToArray());
    }

    private void SetKey(string stepKey)
    {
        if (string.IsNullOrWhiteSpace(stepKey))
        {
            throw new ArgumentNullException(nameof(stepKey));
        }

        _definition.StepKey = stepKey.Trim();
    }

    private void SetHandler(string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentNullException(nameof(methodName));
        }

        _definition.HandlerMethodName = methodName.Trim();
    }

    private void SetHandler(Delegate handler)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (handler.Method.IsStatic)
        {
            throw new ArgumentException("Handler delegate must reference an instance method.", nameof(handler));
        }

        SetHandler(handler.Method.Name);
    }

    // Mutation hooks for the generic typed-message adapter (TypedMessageStep<TEntity, TRequest>), which cannot see
    // the private _definition / SetHandler directly. Same effect as the explicit-interface stage/mode/handler
    // transitions of the permissive chain, just reached from the adapter.
    internal void SetStageInternal(PluginStages stage) => _definition.Stage = stage;
    internal void SetModeInternal(ExecutionMode mode) => _definition.Mode = mode;
    internal void SetHandlerInternal(Delegate handler) => SetHandler(handler);

    // Setting mutations centralised so the four per-terminal IStepConfig implementations stay in lockstep
    // (each explicit implementation is just `Set...(value); return this;`).
    private void SetExecutionOrder(int order) => _definition.ExecutionOrder = order;
    private void SetDescription(string description) => _definition.Description = description ?? string.Empty;
    private void SetImpersonation(ImpersonationSource source) => _definition.ImpersonationSource = source;
    private void SetSupportedDeployment(SupportedDeployment deployment) => _definition.SupportedDeployment = deployment;
    private void SetDisabled() => _definition.Enabled = false;
    private void SetUnsecureConfig(string configuration) => _definition.UnsecureConfig = configuration ?? string.Empty;
    private void SetSecureConfig(string configuration) => _definition.SecureConfig = configuration ?? string.Empty;

    private void StartImage(string alias, bool isPre)
    {
        FinalizeCurrentImage();
        _currentImage = new ImageDefinition { Alias = alias };
        _isPreImage = isPre;
    }

    private void AddImageNow(string alias, bool isPre, Expression<Func<TEntity, object?>>[] attributes)
    {
        StartImage(alias, isPre);
        if (attributes != null && attributes.Length > 0)
        {
            SetCurrentImageAttributes(attributes);
        }
        FinalizeCurrentImage();
    }

    private void SetCurrentImageAttributes(Expression<Func<TEntity, object?>>[] attributes)
    {
        if (_currentImage == null)
        {
            throw new InvalidOperationException("No image is being configured. Call PreImage() or PostImage() first.");
        }
        if (attributes != null && attributes.Length > 0)
        {
            _currentImage.Attributes = ExpressionHelper.GetAttributeNames(attributes);
        }
    }

    private void FinalizeCurrentImage()
    {
        if (_currentImage != null)
        {
            if (_isPreImage)
            {
                _preImages.Add(_currentImage);
            }
            else
            {
                _postImages.Add(_currentImage);
            }
            _currentImage = null;
        }
    }

    private void EnsureImageAllowed(bool isPreImage, string alias)
    {
        string message = _definition.Message ?? string.Empty;
        PluginStages stage = _definition.Stage;
        if (IsImageAllowed(message, stage, isPreImage))
        {
            return;
        }

        string imageType = isPreImage ? "PreImage" : "PostImage";
        throw new InvalidOperationException(
            $"{imageType} '{alias}' is not supported for message '{message}' at stage '{stage}'.");
    }

    private static void EnsureNoDuplicates(string[]? names, string what)
    {
        if (names == null || names.Length == 0)
        {
            return;
        }

        string? duplicate = names
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .FirstOrDefault();

        if (duplicate != null)
        {
            throw new InvalidOperationException($"Duplicate {what}: '{duplicate}' is specified more than once.");
        }
    }

    // Encodes the stage/message rules for which entity images a step may capture. This is the runtime
    // backstop behind the typed builder (the type-state already blocks an invalid image at compile time for
    // the modelled messages). Only the modelled messages (Create/Update/Delete, and the bulk variants
    // evaluated as their single-row counterparts) are enforced; any other message is permissive - Vela does
    // not model its image rules, so the platform arbitrates at deploy time.
    //   * PreValidation / MainOperation: no images exist yet, so none are allowed.
    //   * PreOperation: only a pre-image, and only for Update/Delete (Create has no prior state).
    //   * PostOperation: pre-image for Update/Delete (prior state), post-image for Create/Update
    //     (committed state; Delete has no post-image because the row is gone).
    private static bool IsImageAllowed(string message, PluginStages stage, bool isPreImage)
    {
        // Bulk messages capture per-target image collections under the same stage/message rules as their
        // single-row counterparts, so evaluate them as Create/Update.
        if (string.Equals(message, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase))
        {
            message = PluginMessages.Create;
        }
        else if (string.Equals(message, PluginMessages.UpdateMultiple, StringComparison.OrdinalIgnoreCase))
        {
            message = PluginMessages.Update;
        }

        // Universal rules (platform-wide, independent of the message): no image exists before or outside the
        // operation, and a post-image never exists before the write is committed. These bind even for a
        // non-modelled message, so they are checked before the permissive short-circuit below.
        if (stage == PluginStages.PreValidation || stage == PluginStages.MainOperation)
        {
            return false;
        }
        if (stage == PluginStages.PreOperation && !isPreImage)
        {
            return false;
        }

        bool isCreate = string.Equals(message, PluginMessages.Create, StringComparison.OrdinalIgnoreCase);
        bool isUpdate = string.Equals(message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase);
        bool isDelete = string.Equals(message, PluginMessages.Delete, StringComparison.OrdinalIgnoreCase);

        // Permissive: a message Vela does not model (custom messages, custom APIs, or platform messages such
        // as Merge/Route/Send that Vela does not expose as typed methods). The universal rules above still
        // applied; the message-specific part is trusted here and the platform rejects a genuinely invalid
        // image at deploy time.
        if (!isCreate && !isUpdate && !isDelete)
        {
            return true;
        }

        if (stage == PluginStages.PreOperation)
        {
            // isPreImage is guaranteed true here (the universal rule rejected a pre-operation post-image).
            return isUpdate || isDelete;
        }

        if (stage == PluginStages.PostOperation)
        {
            if (isPreImage)
            {
                return isUpdate || isDelete;
            }

            return isCreate || isUpdate;
        }

        return false;
    }
}
