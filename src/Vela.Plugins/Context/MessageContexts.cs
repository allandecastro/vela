using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Crm.Sdk.Messages;
using Vela.Constants;

namespace Vela.Context
{
    // Per-message typed contexts, split by pipeline stage. A pre-stage handler (PreValidation / PreOperation)
    // receives I<Message>PreContext (typed ctx.Request only); a post-stage handler receives I<Message>PostContext
    // (ctx.Request + ctx.Response). Reading ctx.Response in a pre-stage does not compile. Every message reads its
    // primary record via ctx.Request.Target / ctx.Request.Targets, already typed by the request (an early-bound
    // entity on Create / Update, or an EntityReference / EntityCollection for the SDK requests).

    /// <summary>Pre-stage typed context for a Create step (<c>ctx.Request</c> only).</summary>
    public interface ICreatePreContext<TEntity> : IStepRequestContext<Vela.Messages.CreateRequest<TEntity>> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Create step (<c>ctx.Request</c> + <c>ctx.Response</c> + <c>ctx.PostImage</c>).</summary>
    public interface ICreatePostContext<TEntity> : IStepRequestContext<Vela.Messages.CreateRequest<TEntity>>, IStepResponseContext<CreateResponse>, IPostImageContext<TEntity> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a Update step (<c>ctx.Request</c> + <c>ctx.PreImage</c>).</summary>
    public interface IUpdatePreContext<TEntity> : IStepRequestContext<Vela.Messages.UpdateRequest<TEntity>>, IPreImageContext<TEntity> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Update step (<c>ctx.Request</c> + <c>ctx.Response</c> + <c>ctx.PreImage</c> / <c>ctx.PostImage</c>).</summary>
    public interface IUpdatePostContext<TEntity> : IStepRequestContext<Vela.Messages.UpdateRequest<TEntity>>, IStepResponseContext<UpdateResponse>, IPreImageContext<TEntity>, IPostImageContext<TEntity> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a Delete step (<c>ctx.Request</c> + <c>ctx.PreImage</c>).</summary>
    public interface IDeletePreContext<TEntity> : IStepRequestContext<DeleteRequest>, IPreImageContext<TEntity> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Delete step (<c>ctx.Request</c> + <c>ctx.Response</c> + <c>ctx.PreImage</c>).</summary>
    public interface IDeletePostContext<TEntity> : IStepRequestContext<DeleteRequest>, IStepResponseContext<DeleteResponse>, IPreImageContext<TEntity> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a CreateMultiple step (<c>ctx.Request</c> only).</summary>
    public interface ICreateMultiplePreContext<TEntity> : IStepRequestContext<CreateMultipleRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a CreateMultiple step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ICreateMultiplePostContext<TEntity> : IStepRequestContext<CreateMultipleRequest>, IStepResponseContext<CreateMultipleResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a UpdateMultiple step (<c>ctx.Request</c> only).</summary>
    public interface IUpdateMultiplePreContext<TEntity> : IStepRequestContext<UpdateMultipleRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a UpdateMultiple step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IUpdateMultiplePostContext<TEntity> : IStepRequestContext<UpdateMultipleRequest>, IStepResponseContext<UpdateMultipleResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a Retrieve step (<c>ctx.Request</c> only).</summary>
    public interface IRetrievePreContext<TEntity> : IStepRequestContext<RetrieveRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Retrieve step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRetrievePostContext<TEntity> : IStepRequestContext<RetrieveRequest>, IStepResponseContext<RetrieveResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RetrieveMultiple step (<c>ctx.Request</c> only).</summary>
    public interface IRetrieveMultiplePreContext<TEntity> : IStepRequestContext<RetrieveMultipleRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RetrieveMultiple step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRetrieveMultiplePostContext<TEntity> : IStepRequestContext<RetrieveMultipleRequest>, IStepResponseContext<RetrieveMultipleResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a Associate step (<c>ctx.Request</c> only).</summary>
    public interface IAssociatePreContext<TEntity> : IStepRequestContext<AssociateRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Associate step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IAssociatePostContext<TEntity> : IStepRequestContext<AssociateRequest>, IStepResponseContext<AssociateResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a Disassociate step (<c>ctx.Request</c> only).</summary>
    public interface IDisassociatePreContext<TEntity> : IStepRequestContext<DisassociateRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Disassociate step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IDisassociatePostContext<TEntity> : IStepRequestContext<DisassociateRequest>, IStepResponseContext<DisassociateResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a Merge step (<c>ctx.Request</c> + <c>ctx.PreImage</c> of the master + <c>ctx.SubordinatePreImage</c>).</summary>
    public interface IMergePreContext<TEntity> : IStepRequestContext<MergeRequest>, IPreImageContext<TEntity>, ISubordinatePreImageContext<TEntity> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Merge step (<c>ctx.Request</c> + <c>ctx.Response</c> + the master's <c>ctx.PreImage</c> / <c>ctx.PostImage</c> + the subordinate's <c>ctx.SubordinatePreImage</c> / <c>ctx.SubordinatePostImage</c>).</summary>
    public interface IMergePostContext<TEntity> : IStepRequestContext<MergeRequest>, IStepResponseContext<MergeResponse>, IPreImageContext<TEntity>, IPostImageContext<TEntity>, ISubordinatePreImageContext<TEntity>, ISubordinatePostImageContext<TEntity> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a GrantAccess step (<c>ctx.Request</c> only).</summary>
    public interface IGrantAccessPreContext<TEntity> : IStepRequestContext<GrantAccessRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a GrantAccess step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IGrantAccessPostContext<TEntity> : IStepRequestContext<GrantAccessRequest>, IStepResponseContext<GrantAccessResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for an Assign step (<c>ctx.Request</c> + <c>ctx.PreImage</c>).</summary>
    public interface IAssignPreContext<TEntity> : IStepRequestContext<AssignRequest>, IPreImageContext<TEntity> where TEntity : Entity { }
    /// <summary>Post-stage typed context for an Assign step (<c>ctx.Request</c> + <c>ctx.Response</c> + <c>ctx.PreImage</c> / <c>ctx.PostImage</c>).</summary>
    public interface IAssignPostContext<TEntity> : IStepRequestContext<AssignRequest>, IStepResponseContext<AssignResponse>, IPreImageContext<TEntity>, IPostImageContext<TEntity> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a ModifyAccess step (<c>ctx.Request</c> only).</summary>
    public interface IModifyAccessPreContext<TEntity> : IStepRequestContext<ModifyAccessRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a ModifyAccess step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IModifyAccessPostContext<TEntity> : IStepRequestContext<ModifyAccessRequest>, IStepResponseContext<ModifyAccessResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RevokeAccess step (<c>ctx.Request</c> only).</summary>
    public interface IRevokeAccessPreContext<TEntity> : IStepRequestContext<RevokeAccessRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RevokeAccess step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRevokeAccessPostContext<TEntity> : IStepRequestContext<RevokeAccessRequest>, IStepResponseContext<RevokeAccessResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a QualifyLead step (<c>ctx.Request</c> only).</summary>
    public interface IQualifyLeadPreContext<TEntity> : IStepRequestContext<QualifyLeadRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a QualifyLead step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IQualifyLeadPostContext<TEntity> : IStepRequestContext<QualifyLeadRequest>, IStepResponseContext<QualifyLeadResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a WinOpportunity step (<c>ctx.Request</c> only).</summary>
    public interface IWinOpportunityPreContext<TEntity> : IStepRequestContext<WinOpportunityRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a WinOpportunity step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IWinOpportunityPostContext<TEntity> : IStepRequestContext<WinOpportunityRequest>, IStepResponseContext<WinOpportunityResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a LoseOpportunity step (<c>ctx.Request</c> only).</summary>
    public interface ILoseOpportunityPreContext<TEntity> : IStepRequestContext<LoseOpportunityRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a LoseOpportunity step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ILoseOpportunityPostContext<TEntity> : IStepRequestContext<LoseOpportunityRequest>, IStepResponseContext<LoseOpportunityResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a ConvertSalesOrderToInvoice step (<c>ctx.Request</c> only).</summary>
    public interface IConvertSalesOrderToInvoicePreContext<TEntity> : IStepRequestContext<ConvertSalesOrderToInvoiceRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a ConvertSalesOrderToInvoice step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IConvertSalesOrderToInvoicePostContext<TEntity> : IStepRequestContext<ConvertSalesOrderToInvoiceRequest>, IStepResponseContext<ConvertSalesOrderToInvoiceResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a GenerateQuoteFromOpportunity step (<c>ctx.Request</c> only).</summary>
    public interface IGenerateQuoteFromOpportunityPreContext<TEntity> : IStepRequestContext<GenerateQuoteFromOpportunityRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a GenerateQuoteFromOpportunity step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IGenerateQuoteFromOpportunityPostContext<TEntity> : IStepRequestContext<GenerateQuoteFromOpportunityRequest>, IStepResponseContext<GenerateQuoteFromOpportunityResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a AddToQueue step (<c>ctx.Request</c> only).</summary>
    public interface IAddToQueuePreContext<TEntity> : IStepRequestContext<AddToQueueRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a AddToQueue step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IAddToQueuePostContext<TEntity> : IStepRequestContext<AddToQueueRequest>, IStepResponseContext<AddToQueueResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RemoveFromQueue step (<c>ctx.Request</c> only).</summary>
    public interface IRemoveFromQueuePreContext<TEntity> : IStepRequestContext<RemoveFromQueueRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RemoveFromQueue step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRemoveFromQueuePostContext<TEntity> : IStepRequestContext<RemoveFromQueueRequest>, IStepResponseContext<RemoveFromQueueResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a PickFromQueue step (<c>ctx.Request</c> only).</summary>
    public interface IPickFromQueuePreContext<TEntity> : IStepRequestContext<PickFromQueueRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a PickFromQueue step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IPickFromQueuePostContext<TEntity> : IStepRequestContext<PickFromQueueRequest>, IStepResponseContext<PickFromQueueResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RouteTo step (<c>ctx.Request</c> only).</summary>
    public interface IRouteToPreContext<TEntity> : IStepRequestContext<RouteToRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RouteTo step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRouteToPostContext<TEntity> : IStepRequestContext<RouteToRequest>, IStepResponseContext<RouteToResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a ReleaseToQueue step (<c>ctx.Request</c> only).</summary>
    public interface IReleaseToQueuePreContext<TEntity> : IStepRequestContext<ReleaseToQueueRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a ReleaseToQueue step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IReleaseToQueuePostContext<TEntity> : IStepRequestContext<ReleaseToQueueRequest>, IStepResponseContext<ReleaseToQueueResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a SendEmail step (<c>ctx.Request</c> only).</summary>
    public interface ISendEmailPreContext<TEntity> : IStepRequestContext<SendEmailRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a SendEmail step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ISendEmailPostContext<TEntity> : IStepRequestContext<SendEmailRequest>, IStepResponseContext<SendEmailResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a SendEmailFromTemplate step (<c>ctx.Request</c> only).</summary>
    public interface ISendEmailFromTemplatePreContext<TEntity> : IStepRequestContext<Vela.Messages.SendEmailFromTemplateRequest<TEntity>> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a SendEmailFromTemplate step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ISendEmailFromTemplatePostContext<TEntity> : IStepRequestContext<Vela.Messages.SendEmailFromTemplateRequest<TEntity>>, IStepResponseContext<SendEmailFromTemplateResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a DeliverIncomingEmail step (<c>ctx.Request</c> only).</summary>
    public interface IDeliverIncomingEmailPreContext<TEntity> : IStepRequestContext<DeliverIncomingEmailRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a DeliverIncomingEmail step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IDeliverIncomingEmailPostContext<TEntity> : IStepRequestContext<DeliverIncomingEmailRequest>, IStepResponseContext<DeliverIncomingEmailResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a DeliverPromoteEmail step (<c>ctx.Request</c> only).</summary>
    public interface IDeliverPromoteEmailPreContext<TEntity> : IStepRequestContext<DeliverPromoteEmailRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a DeliverPromoteEmail step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IDeliverPromoteEmailPostContext<TEntity> : IStepRequestContext<DeliverPromoteEmailRequest>, IStepResponseContext<DeliverPromoteEmailResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a AddMembersTeam step (<c>ctx.Request</c> only).</summary>
    public interface IAddMembersTeamPreContext<TEntity> : IStepRequestContext<AddMembersTeamRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a AddMembersTeam step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IAddMembersTeamPostContext<TEntity> : IStepRequestContext<AddMembersTeamRequest>, IStepResponseContext<AddMembersTeamResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RemoveMembersTeam step (<c>ctx.Request</c> only).</summary>
    public interface IRemoveMembersTeamPreContext<TEntity> : IStepRequestContext<RemoveMembersTeamRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RemoveMembersTeam step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRemoveMembersTeamPostContext<TEntity> : IStepRequestContext<RemoveMembersTeamRequest>, IStepResponseContext<RemoveMembersTeamResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a AddListMembersList step (<c>ctx.Request</c> only).</summary>
    public interface IAddListMembersListPreContext<TEntity> : IStepRequestContext<AddListMembersListRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a AddListMembersList step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IAddListMembersListPostContext<TEntity> : IStepRequestContext<AddListMembersListRequest>, IStepResponseContext<AddListMembersListResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a AddMemberList step (<c>ctx.Request</c> only).</summary>
    public interface IAddMemberListPreContext<TEntity> : IStepRequestContext<AddMemberListRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a AddMemberList step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IAddMemberListPostContext<TEntity> : IStepRequestContext<AddMemberListRequest>, IStepResponseContext<AddMemberListResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RemoveMemberList step (<c>ctx.Request</c> only).</summary>
    public interface IRemoveMemberListPreContext<TEntity> : IStepRequestContext<RemoveMemberListRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RemoveMemberList step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRemoveMemberListPostContext<TEntity> : IStepRequestContext<RemoveMemberListRequest>, IStepResponseContext<RemoveMemberListResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a CloseIncident step (<c>ctx.Request</c> only).</summary>
    public interface ICloseIncidentPreContext<TEntity> : IStepRequestContext<CloseIncidentRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a CloseIncident step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ICloseIncidentPostContext<TEntity> : IStepRequestContext<CloseIncidentRequest>, IStepResponseContext<CloseIncidentResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a CancelContract step (<c>ctx.Request</c> only).</summary>
    public interface ICancelContractPreContext<TEntity> : IStepRequestContext<CancelContractRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a CancelContract step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ICancelContractPostContext<TEntity> : IStepRequestContext<CancelContractRequest>, IStepResponseContext<CancelContractResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a CloneContract step (<c>ctx.Request</c> only).</summary>
    public interface ICloneContractPreContext<TEntity> : IStepRequestContext<CloneContractRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a CloneContract step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ICloneContractPostContext<TEntity> : IStepRequestContext<CloneContractRequest>, IStepResponseContext<CloneContractResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RetrievePrincipalAccess step (<c>ctx.Request</c> only).</summary>
    public interface IRetrievePrincipalAccessPreContext<TEntity> : IStepRequestContext<RetrievePrincipalAccessRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RetrievePrincipalAccess step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRetrievePrincipalAccessPostContext<TEntity> : IStepRequestContext<RetrievePrincipalAccessRequest>, IStepResponseContext<RetrievePrincipalAccessResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RetrieveSharedPrincipalsAndAccess step (<c>ctx.Request</c> only).</summary>
    public interface IRetrieveSharedPrincipalsAndAccessPreContext<TEntity> : IStepRequestContext<RetrieveSharedPrincipalsAndAccessRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RetrieveSharedPrincipalsAndAccess step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRetrieveSharedPrincipalsAndAccessPostContext<TEntity> : IStepRequestContext<RetrieveSharedPrincipalsAndAccessRequest>, IStepResponseContext<RetrieveSharedPrincipalsAndAccessResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a WinQuote step (<c>ctx.Request</c> only).</summary>
    public interface IWinQuotePreContext<TEntity> : IStepRequestContext<WinQuoteRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a WinQuote step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IWinQuotePostContext<TEntity> : IStepRequestContext<WinQuoteRequest>, IStepResponseContext<WinQuoteResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a AddItemCampaign step (<c>ctx.Request</c> only).</summary>
    public interface IAddItemCampaignPreContext<TEntity> : IStepRequestContext<AddItemCampaignRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a AddItemCampaign step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IAddItemCampaignPostContext<TEntity> : IStepRequestContext<AddItemCampaignRequest>, IStepResponseContext<AddItemCampaignResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RemoveItemCampaign step (<c>ctx.Request</c> only).</summary>
    public interface IRemoveItemCampaignPreContext<TEntity> : IStepRequestContext<RemoveItemCampaignRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RemoveItemCampaign step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRemoveItemCampaignPostContext<TEntity> : IStepRequestContext<RemoveItemCampaignRequest>, IStepResponseContext<RemoveItemCampaignResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a Recalculate step (<c>ctx.Request</c> only).</summary>
    public interface IRecalculatePreContext<TEntity> : IStepRequestContext<RecalculateRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Recalculate step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRecalculatePostContext<TEntity> : IStepRequestContext<RecalculateRequest>, IStepResponseContext<RecalculateResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a LockSalesOrderPricing step (<c>ctx.Request</c> only).</summary>
    public interface ILockSalesOrderPricingPreContext<TEntity> : IStepRequestContext<LockSalesOrderPricingRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a LockSalesOrderPricing step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ILockSalesOrderPricingPostContext<TEntity> : IStepRequestContext<LockSalesOrderPricingRequest>, IStepResponseContext<LockSalesOrderPricingResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a UnlockSalesOrderPricing step (<c>ctx.Request</c> only).</summary>
    public interface IUnlockSalesOrderPricingPreContext<TEntity> : IStepRequestContext<UnlockSalesOrderPricingRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a UnlockSalesOrderPricing step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IUnlockSalesOrderPricingPostContext<TEntity> : IStepRequestContext<UnlockSalesOrderPricingRequest>, IStepResponseContext<UnlockSalesOrderPricingResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a LockInvoicePricing step (<c>ctx.Request</c> only).</summary>
    public interface ILockInvoicePricingPreContext<TEntity> : IStepRequestContext<LockInvoicePricingRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a LockInvoicePricing step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ILockInvoicePricingPostContext<TEntity> : IStepRequestContext<LockInvoicePricingRequest>, IStepResponseContext<LockInvoicePricingResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a UnlockInvoicePricing step (<c>ctx.Request</c> only).</summary>
    public interface IUnlockInvoicePricingPreContext<TEntity> : IStepRequestContext<UnlockInvoicePricingRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a UnlockInvoicePricing step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IUnlockInvoicePricingPostContext<TEntity> : IStepRequestContext<UnlockInvoicePricingRequest>, IStepResponseContext<UnlockInvoicePricingResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a Reschedule step (<c>ctx.Request</c> only).</summary>
    public interface IReschedulePreContext<TEntity> : IStepRequestContext<RescheduleRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Reschedule step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IReschedulePostContext<TEntity> : IStepRequestContext<RescheduleRequest>, IStepResponseContext<RescheduleResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a ImmediateBook step (<c>ctx.Request</c> only).</summary>
    public interface IImmediateBookPreContext<TEntity> : IStepRequestContext<ImmediateBookRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a ImmediateBook step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IImmediateBookPostContext<TEntity> : IStepRequestContext<ImmediateBookRequest>, IStepResponseContext<ImmediateBookResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a SendTemplate step (<c>ctx.Request</c> only).</summary>
    public interface ISendTemplatePreContext<TEntity> : IStepRequestContext<SendTemplateRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a SendTemplate step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ISendTemplatePostContext<TEntity> : IStepRequestContext<SendTemplateRequest>, IStepResponseContext<SendTemplateResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a SendFax step (<c>ctx.Request</c> only).</summary>
    public interface ISendFaxPreContext<TEntity> : IStepRequestContext<SendFaxRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a SendFax step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface ISendFaxPostContext<TEntity> : IStepRequestContext<SendFaxRequest>, IStepResponseContext<SendFaxResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a AddProductToKit step (<c>ctx.Request</c> only).</summary>
    public interface IAddProductToKitPreContext<TEntity> : IStepRequestContext<AddProductToKitRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a AddProductToKit step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IAddProductToKitPostContext<TEntity> : IStepRequestContext<AddProductToKitRequest>, IStepResponseContext<AddProductToKitResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a RemoveProductFromKit step (<c>ctx.Request</c> only).</summary>
    public interface IRemoveProductFromKitPreContext<TEntity> : IStepRequestContext<RemoveProductFromKitRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a RemoveProductFromKit step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IRemoveProductFromKitPostContext<TEntity> : IStepRequestContext<RemoveProductFromKitRequest>, IStepResponseContext<RemoveProductFromKitResponse> where TEntity : Entity { }

    /// <summary>Runtime pre-stage context for Create.</summary>
    public class CreatePreContext<TEntity> : PreStepContext<Vela.Messages.CreateRequest<TEntity>>, ICreatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Create.</summary>
    public class CreatePostContext<TEntity> : PostStepContext<Vela.Messages.CreateRequest<TEntity>, CreateResponse>, ICreatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreatePostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PostImage => this.GetPostImage<TEntity>();
    }
    /// <summary>Runtime pre-stage context for Update.</summary>
    public class UpdatePreContext<TEntity> : PreStepContext<Vela.Messages.UpdateRequest<TEntity>>, IUpdatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdatePreContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
    }
    /// <summary>Runtime post-stage context for Update.</summary>
    public class UpdatePostContext<TEntity> : PostStepContext<Vela.Messages.UpdateRequest<TEntity>, UpdateResponse>, IUpdatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdatePostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
        /// <inheritdoc/>
        public TEntity? PostImage => this.GetPostImage<TEntity>();
    }
    /// <summary>Runtime pre-stage context for Delete.</summary>
    public class DeletePreContext<TEntity> : PreStepContext<DeleteRequest>, IDeletePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeletePreContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
    }
    /// <summary>Runtime post-stage context for Delete.</summary>
    public class DeletePostContext<TEntity> : PostStepContext<DeleteRequest, DeleteResponse>, IDeletePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeletePostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
    }
    /// <summary>Runtime pre-stage context for CreateMultiple.</summary>
    public class CreateMultiplePreContext<TEntity> : PreStepContext<CreateMultipleRequest>, ICreateMultiplePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreateMultiplePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for CreateMultiple.</summary>
    public class CreateMultiplePostContext<TEntity> : PostStepContext<CreateMultipleRequest, CreateMultipleResponse>, ICreateMultiplePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreateMultiplePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for UpdateMultiple.</summary>
    public class UpdateMultiplePreContext<TEntity> : PreStepContext<UpdateMultipleRequest>, IUpdateMultiplePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdateMultiplePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for UpdateMultiple.</summary>
    public class UpdateMultiplePostContext<TEntity> : PostStepContext<UpdateMultipleRequest, UpdateMultipleResponse>, IUpdateMultiplePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdateMultiplePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Retrieve.</summary>
    public class RetrievePreContext<TEntity> : PreStepContext<RetrieveRequest>, IRetrievePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrievePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Retrieve.</summary>
    public class RetrievePostContext<TEntity> : PostStepContext<RetrieveRequest, RetrieveResponse>, IRetrievePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrievePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RetrieveMultiple.</summary>
    public class RetrieveMultiplePreContext<TEntity> : PreStepContext<RetrieveMultipleRequest>, IRetrieveMultiplePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveMultiplePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RetrieveMultiple.</summary>
    public class RetrieveMultiplePostContext<TEntity> : PostStepContext<RetrieveMultipleRequest, RetrieveMultipleResponse>, IRetrieveMultiplePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveMultiplePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Associate.</summary>
    public class AssociatePreContext<TEntity> : PreStepContext<AssociateRequest>, IAssociatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AssociatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Associate.</summary>
    public class AssociatePostContext<TEntity> : PostStepContext<AssociateRequest, AssociateResponse>, IAssociatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AssociatePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Disassociate.</summary>
    public class DisassociatePreContext<TEntity> : PreStepContext<DisassociateRequest>, IDisassociatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DisassociatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Disassociate.</summary>
    public class DisassociatePostContext<TEntity> : PostStepContext<DisassociateRequest, DisassociateResponse>, IDisassociatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DisassociatePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Merge.</summary>
    public class MergePreContext<TEntity> : PreStepContext<MergeRequest>, IMergePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MergePreContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
        /// <inheritdoc/>
        public TEntity? SubordinatePreImage => this.GetPreImage<TEntity>(PluginImageAliases.SubordinatePreImage);
    }
    /// <summary>Runtime post-stage context for Merge.</summary>
    public class MergePostContext<TEntity> : PostStepContext<MergeRequest, MergeResponse>, IMergePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MergePostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
        /// <inheritdoc/>
        public TEntity? PostImage => this.GetPostImage<TEntity>();
        /// <inheritdoc/>
        public TEntity? SubordinatePreImage => this.GetPreImage<TEntity>(PluginImageAliases.SubordinatePreImage);
        /// <inheritdoc/>
        public TEntity? SubordinatePostImage => this.GetPostImage<TEntity>(PluginImageAliases.SubordinatePostImage);
    }
    /// <summary>Runtime pre-stage context for GrantAccess.</summary>
    public class GrantAccessPreContext<TEntity> : PreStepContext<GrantAccessRequest>, IGrantAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GrantAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for GrantAccess.</summary>
    public class GrantAccessPostContext<TEntity> : PostStepContext<GrantAccessRequest, GrantAccessResponse>, IGrantAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GrantAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Assign.</summary>
    public class AssignPreContext<TEntity> : PreStepContext<AssignRequest>, IAssignPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AssignPreContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
    }
    /// <summary>Runtime post-stage context for Assign.</summary>
    public class AssignPostContext<TEntity> : PostStepContext<AssignRequest, AssignResponse>, IAssignPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AssignPostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
        /// <inheritdoc/>
        public TEntity? PostImage => this.GetPostImage<TEntity>();
    }
    /// <summary>Runtime pre-stage context for ModifyAccess.</summary>
    public class ModifyAccessPreContext<TEntity> : PreStepContext<ModifyAccessRequest>, IModifyAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ModifyAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for ModifyAccess.</summary>
    public class ModifyAccessPostContext<TEntity> : PostStepContext<ModifyAccessRequest, ModifyAccessResponse>, IModifyAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ModifyAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RevokeAccess.</summary>
    public class RevokeAccessPreContext<TEntity> : PreStepContext<RevokeAccessRequest>, IRevokeAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RevokeAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RevokeAccess.</summary>
    public class RevokeAccessPostContext<TEntity> : PostStepContext<RevokeAccessRequest, RevokeAccessResponse>, IRevokeAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RevokeAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for QualifyLead.</summary>
    public class QualifyLeadPreContext<TEntity> : PreStepContext<QualifyLeadRequest>, IQualifyLeadPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public QualifyLeadPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for QualifyLead.</summary>
    public class QualifyLeadPostContext<TEntity> : PostStepContext<QualifyLeadRequest, QualifyLeadResponse>, IQualifyLeadPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public QualifyLeadPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for WinOpportunity.</summary>
    public class WinOpportunityPreContext<TEntity> : PreStepContext<WinOpportunityRequest>, IWinOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public WinOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for WinOpportunity.</summary>
    public class WinOpportunityPostContext<TEntity> : PostStepContext<WinOpportunityRequest, WinOpportunityResponse>, IWinOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public WinOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for LoseOpportunity.</summary>
    public class LoseOpportunityPreContext<TEntity> : PreStepContext<LoseOpportunityRequest>, ILoseOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public LoseOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for LoseOpportunity.</summary>
    public class LoseOpportunityPostContext<TEntity> : PostStepContext<LoseOpportunityRequest, LoseOpportunityResponse>, ILoseOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public LoseOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for ConvertSalesOrderToInvoice.</summary>
    public class ConvertSalesOrderToInvoicePreContext<TEntity> : PreStepContext<ConvertSalesOrderToInvoiceRequest>, IConvertSalesOrderToInvoicePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ConvertSalesOrderToInvoicePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for ConvertSalesOrderToInvoice.</summary>
    public class ConvertSalesOrderToInvoicePostContext<TEntity> : PostStepContext<ConvertSalesOrderToInvoiceRequest, ConvertSalesOrderToInvoiceResponse>, IConvertSalesOrderToInvoicePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ConvertSalesOrderToInvoicePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for GenerateQuoteFromOpportunity.</summary>
    public class GenerateQuoteFromOpportunityPreContext<TEntity> : PreStepContext<GenerateQuoteFromOpportunityRequest>, IGenerateQuoteFromOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateQuoteFromOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for GenerateQuoteFromOpportunity.</summary>
    public class GenerateQuoteFromOpportunityPostContext<TEntity> : PostStepContext<GenerateQuoteFromOpportunityRequest, GenerateQuoteFromOpportunityResponse>, IGenerateQuoteFromOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateQuoteFromOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddToQueue.</summary>
    public class AddToQueuePreContext<TEntity> : PreStepContext<AddToQueueRequest>, IAddToQueuePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddToQueuePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddToQueue.</summary>
    public class AddToQueuePostContext<TEntity> : PostStepContext<AddToQueueRequest, AddToQueueResponse>, IAddToQueuePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddToQueuePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RemoveFromQueue.</summary>
    public class RemoveFromQueuePreContext<TEntity> : PreStepContext<RemoveFromQueueRequest>, IRemoveFromQueuePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveFromQueuePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RemoveFromQueue.</summary>
    public class RemoveFromQueuePostContext<TEntity> : PostStepContext<RemoveFromQueueRequest, RemoveFromQueueResponse>, IRemoveFromQueuePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveFromQueuePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for PickFromQueue.</summary>
    public class PickFromQueuePreContext<TEntity> : PreStepContext<PickFromQueueRequest>, IPickFromQueuePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public PickFromQueuePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for PickFromQueue.</summary>
    public class PickFromQueuePostContext<TEntity> : PostStepContext<PickFromQueueRequest, PickFromQueueResponse>, IPickFromQueuePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public PickFromQueuePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RouteTo.</summary>
    public class RouteToPreContext<TEntity> : PreStepContext<RouteToRequest>, IRouteToPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RouteToPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RouteTo.</summary>
    public class RouteToPostContext<TEntity> : PostStepContext<RouteToRequest, RouteToResponse>, IRouteToPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RouteToPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for ReleaseToQueue.</summary>
    public class ReleaseToQueuePreContext<TEntity> : PreStepContext<ReleaseToQueueRequest>, IReleaseToQueuePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ReleaseToQueuePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for ReleaseToQueue.</summary>
    public class ReleaseToQueuePostContext<TEntity> : PostStepContext<ReleaseToQueueRequest, ReleaseToQueueResponse>, IReleaseToQueuePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ReleaseToQueuePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for SendEmail.</summary>
    public class SendEmailPreContext<TEntity> : PreStepContext<SendEmailRequest>, ISendEmailPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for SendEmail.</summary>
    public class SendEmailPostContext<TEntity> : PostStepContext<SendEmailRequest, SendEmailResponse>, ISendEmailPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for SendEmailFromTemplate.</summary>
    public class SendEmailFromTemplatePreContext<TEntity> : PreStepContext<Vela.Messages.SendEmailFromTemplateRequest<TEntity>>, ISendEmailFromTemplatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailFromTemplatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for SendEmailFromTemplate.</summary>
    public class SendEmailFromTemplatePostContext<TEntity> : PostStepContext<Vela.Messages.SendEmailFromTemplateRequest<TEntity>, SendEmailFromTemplateResponse>, ISendEmailFromTemplatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailFromTemplatePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for DeliverIncomingEmail.</summary>
    public class DeliverIncomingEmailPreContext<TEntity> : PreStepContext<DeliverIncomingEmailRequest>, IDeliverIncomingEmailPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverIncomingEmailPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for DeliverIncomingEmail.</summary>
    public class DeliverIncomingEmailPostContext<TEntity> : PostStepContext<DeliverIncomingEmailRequest, DeliverIncomingEmailResponse>, IDeliverIncomingEmailPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverIncomingEmailPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for DeliverPromoteEmail.</summary>
    public class DeliverPromoteEmailPreContext<TEntity> : PreStepContext<DeliverPromoteEmailRequest>, IDeliverPromoteEmailPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverPromoteEmailPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for DeliverPromoteEmail.</summary>
    public class DeliverPromoteEmailPostContext<TEntity> : PostStepContext<DeliverPromoteEmailRequest, DeliverPromoteEmailResponse>, IDeliverPromoteEmailPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverPromoteEmailPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddMembersTeam.</summary>
    public class AddMembersTeamPreContext<TEntity> : PreStepContext<AddMembersTeamRequest>, IAddMembersTeamPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMembersTeamPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddMembersTeam.</summary>
    public class AddMembersTeamPostContext<TEntity> : PostStepContext<AddMembersTeamRequest, AddMembersTeamResponse>, IAddMembersTeamPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMembersTeamPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RemoveMembersTeam.</summary>
    public class RemoveMembersTeamPreContext<TEntity> : PreStepContext<RemoveMembersTeamRequest>, IRemoveMembersTeamPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMembersTeamPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RemoveMembersTeam.</summary>
    public class RemoveMembersTeamPostContext<TEntity> : PostStepContext<RemoveMembersTeamRequest, RemoveMembersTeamResponse>, IRemoveMembersTeamPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMembersTeamPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddListMembersList.</summary>
    public class AddListMembersListPreContext<TEntity> : PreStepContext<AddListMembersListRequest>, IAddListMembersListPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddListMembersListPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddListMembersList.</summary>
    public class AddListMembersListPostContext<TEntity> : PostStepContext<AddListMembersListRequest, AddListMembersListResponse>, IAddListMembersListPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddListMembersListPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddMemberList.</summary>
    public class AddMemberListPreContext<TEntity> : PreStepContext<AddMemberListRequest>, IAddMemberListPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMemberListPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddMemberList.</summary>
    public class AddMemberListPostContext<TEntity> : PostStepContext<AddMemberListRequest, AddMemberListResponse>, IAddMemberListPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMemberListPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RemoveMemberList.</summary>
    public class RemoveMemberListPreContext<TEntity> : PreStepContext<RemoveMemberListRequest>, IRemoveMemberListPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMemberListPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RemoveMemberList.</summary>
    public class RemoveMemberListPostContext<TEntity> : PostStepContext<RemoveMemberListRequest, RemoveMemberListResponse>, IRemoveMemberListPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMemberListPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for CloseIncident.</summary>
    public class CloseIncidentPreContext<TEntity> : PreStepContext<CloseIncidentRequest>, ICloseIncidentPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloseIncidentPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for CloseIncident.</summary>
    public class CloseIncidentPostContext<TEntity> : PostStepContext<CloseIncidentRequest, CloseIncidentResponse>, ICloseIncidentPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloseIncidentPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for CancelContract.</summary>
    public class CancelContractPreContext<TEntity> : PreStepContext<CancelContractRequest>, ICancelContractPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CancelContractPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for CancelContract.</summary>
    public class CancelContractPostContext<TEntity> : PostStepContext<CancelContractRequest, CancelContractResponse>, ICancelContractPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CancelContractPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for CloneContract.</summary>
    public class CloneContractPreContext<TEntity> : PreStepContext<CloneContractRequest>, ICloneContractPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloneContractPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for CloneContract.</summary>
    public class CloneContractPostContext<TEntity> : PostStepContext<CloneContractRequest, CloneContractResponse>, ICloneContractPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloneContractPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RetrievePrincipalAccess.</summary>
    public class RetrievePrincipalAccessPreContext<TEntity> : PreStepContext<RetrievePrincipalAccessRequest>, IRetrievePrincipalAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrievePrincipalAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RetrievePrincipalAccess.</summary>
    public class RetrievePrincipalAccessPostContext<TEntity> : PostStepContext<RetrievePrincipalAccessRequest, RetrievePrincipalAccessResponse>, IRetrievePrincipalAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrievePrincipalAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RetrieveSharedPrincipalsAndAccess.</summary>
    public class RetrieveSharedPrincipalsAndAccessPreContext<TEntity> : PreStepContext<RetrieveSharedPrincipalsAndAccessRequest>, IRetrieveSharedPrincipalsAndAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveSharedPrincipalsAndAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RetrieveSharedPrincipalsAndAccess.</summary>
    public class RetrieveSharedPrincipalsAndAccessPostContext<TEntity> : PostStepContext<RetrieveSharedPrincipalsAndAccessRequest, RetrieveSharedPrincipalsAndAccessResponse>, IRetrieveSharedPrincipalsAndAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveSharedPrincipalsAndAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for WinQuote.</summary>
    public class WinQuotePreContext<TEntity> : PreStepContext<WinQuoteRequest>, IWinQuotePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public WinQuotePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for WinQuote.</summary>
    public class WinQuotePostContext<TEntity> : PostStepContext<WinQuoteRequest, WinQuoteResponse>, IWinQuotePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public WinQuotePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddItemCampaign.</summary>
    public class AddItemCampaignPreContext<TEntity> : PreStepContext<AddItemCampaignRequest>, IAddItemCampaignPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddItemCampaignPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddItemCampaign.</summary>
    public class AddItemCampaignPostContext<TEntity> : PostStepContext<AddItemCampaignRequest, AddItemCampaignResponse>, IAddItemCampaignPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddItemCampaignPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RemoveItemCampaign.</summary>
    public class RemoveItemCampaignPreContext<TEntity> : PreStepContext<RemoveItemCampaignRequest>, IRemoveItemCampaignPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveItemCampaignPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RemoveItemCampaign.</summary>
    public class RemoveItemCampaignPostContext<TEntity> : PostStepContext<RemoveItemCampaignRequest, RemoveItemCampaignResponse>, IRemoveItemCampaignPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveItemCampaignPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Recalculate.</summary>
    public class RecalculatePreContext<TEntity> : PreStepContext<RecalculateRequest>, IRecalculatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RecalculatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Recalculate.</summary>
    public class RecalculatePostContext<TEntity> : PostStepContext<RecalculateRequest, RecalculateResponse>, IRecalculatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RecalculatePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for LockSalesOrderPricing.</summary>
    public class LockSalesOrderPricingPreContext<TEntity> : PreStepContext<LockSalesOrderPricingRequest>, ILockSalesOrderPricingPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public LockSalesOrderPricingPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for LockSalesOrderPricing.</summary>
    public class LockSalesOrderPricingPostContext<TEntity> : PostStepContext<LockSalesOrderPricingRequest, LockSalesOrderPricingResponse>, ILockSalesOrderPricingPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public LockSalesOrderPricingPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for UnlockSalesOrderPricing.</summary>
    public class UnlockSalesOrderPricingPreContext<TEntity> : PreStepContext<UnlockSalesOrderPricingRequest>, IUnlockSalesOrderPricingPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UnlockSalesOrderPricingPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for UnlockSalesOrderPricing.</summary>
    public class UnlockSalesOrderPricingPostContext<TEntity> : PostStepContext<UnlockSalesOrderPricingRequest, UnlockSalesOrderPricingResponse>, IUnlockSalesOrderPricingPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UnlockSalesOrderPricingPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for LockInvoicePricing.</summary>
    public class LockInvoicePricingPreContext<TEntity> : PreStepContext<LockInvoicePricingRequest>, ILockInvoicePricingPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public LockInvoicePricingPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for LockInvoicePricing.</summary>
    public class LockInvoicePricingPostContext<TEntity> : PostStepContext<LockInvoicePricingRequest, LockInvoicePricingResponse>, ILockInvoicePricingPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public LockInvoicePricingPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for UnlockInvoicePricing.</summary>
    public class UnlockInvoicePricingPreContext<TEntity> : PreStepContext<UnlockInvoicePricingRequest>, IUnlockInvoicePricingPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UnlockInvoicePricingPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for UnlockInvoicePricing.</summary>
    public class UnlockInvoicePricingPostContext<TEntity> : PostStepContext<UnlockInvoicePricingRequest, UnlockInvoicePricingResponse>, IUnlockInvoicePricingPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UnlockInvoicePricingPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Reschedule.</summary>
    public class ReschedulePreContext<TEntity> : PreStepContext<RescheduleRequest>, IReschedulePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ReschedulePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Reschedule.</summary>
    public class ReschedulePostContext<TEntity> : PostStepContext<RescheduleRequest, RescheduleResponse>, IReschedulePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ReschedulePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for ImmediateBook.</summary>
    public class ImmediateBookPreContext<TEntity> : PreStepContext<ImmediateBookRequest>, IImmediateBookPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ImmediateBookPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for ImmediateBook.</summary>
    public class ImmediateBookPostContext<TEntity> : PostStepContext<ImmediateBookRequest, ImmediateBookResponse>, IImmediateBookPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ImmediateBookPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for SendTemplate.</summary>
    public class SendTemplatePreContext<TEntity> : PreStepContext<SendTemplateRequest>, ISendTemplatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendTemplatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for SendTemplate.</summary>
    public class SendTemplatePostContext<TEntity> : PostStepContext<SendTemplateRequest, SendTemplateResponse>, ISendTemplatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendTemplatePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for SendFax.</summary>
    public class SendFaxPreContext<TEntity> : PreStepContext<SendFaxRequest>, ISendFaxPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendFaxPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for SendFax.</summary>
    public class SendFaxPostContext<TEntity> : PostStepContext<SendFaxRequest, SendFaxResponse>, ISendFaxPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendFaxPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddProductToKit.</summary>
    public class AddProductToKitPreContext<TEntity> : PreStepContext<AddProductToKitRequest>, IAddProductToKitPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddProductToKitPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddProductToKit.</summary>
    public class AddProductToKitPostContext<TEntity> : PostStepContext<AddProductToKitRequest, AddProductToKitResponse>, IAddProductToKitPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddProductToKitPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RemoveProductFromKit.</summary>
    public class RemoveProductFromKitPreContext<TEntity> : PreStepContext<RemoveProductFromKitRequest>, IRemoveProductFromKitPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveProductFromKitPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RemoveProductFromKit.</summary>
    public class RemoveProductFromKitPostContext<TEntity> : PostStepContext<RemoveProductFromKitRequest, RemoveProductFromKitResponse>, IRemoveProductFromKitPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveProductFromKitPostContext(ILocalPluginContext inner) : base(inner) { }
    }
}
