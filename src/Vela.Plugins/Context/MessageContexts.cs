using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Crm.Sdk.Messages;

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
    /// <summary>Pre-stage typed context for a Merge step (<c>ctx.Request</c> only).</summary>
    public interface IMergePreContext<TEntity> : IStepRequestContext<MergeRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a Merge step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IMergePostContext<TEntity> : IStepRequestContext<MergeRequest>, IStepResponseContext<MergeResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a GrantAccess step (<c>ctx.Request</c> only).</summary>
    public interface IGrantAccessPreContext<TEntity> : IStepRequestContext<GrantAccessRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a GrantAccess step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IGrantAccessPostContext<TEntity> : IStepRequestContext<GrantAccessRequest>, IStepResponseContext<GrantAccessResponse> where TEntity : Entity { }
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
    /// <summary>Pre-stage typed context for a ConvertQuoteToSalesOrder step (<c>ctx.Request</c> only).</summary>
    public interface IConvertQuoteToSalesOrderPreContext<TEntity> : IStepRequestContext<ConvertQuoteToSalesOrderRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a ConvertQuoteToSalesOrder step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IConvertQuoteToSalesOrderPostContext<TEntity> : IStepRequestContext<ConvertQuoteToSalesOrderRequest>, IStepResponseContext<ConvertQuoteToSalesOrderResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a ConvertSalesOrderToInvoice step (<c>ctx.Request</c> only).</summary>
    public interface IConvertSalesOrderToInvoicePreContext<TEntity> : IStepRequestContext<ConvertSalesOrderToInvoiceRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a ConvertSalesOrderToInvoice step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IConvertSalesOrderToInvoicePostContext<TEntity> : IStepRequestContext<ConvertSalesOrderToInvoiceRequest>, IStepResponseContext<ConvertSalesOrderToInvoiceResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a GenerateQuoteFromOpportunity step (<c>ctx.Request</c> only).</summary>
    public interface IGenerateQuoteFromOpportunityPreContext<TEntity> : IStepRequestContext<GenerateQuoteFromOpportunityRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a GenerateQuoteFromOpportunity step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IGenerateQuoteFromOpportunityPostContext<TEntity> : IStepRequestContext<GenerateQuoteFromOpportunityRequest>, IStepResponseContext<GenerateQuoteFromOpportunityResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a GenerateSalesOrderFromOpportunity step (<c>ctx.Request</c> only).</summary>
    public interface IGenerateSalesOrderFromOpportunityPreContext<TEntity> : IStepRequestContext<GenerateSalesOrderFromOpportunityRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a GenerateSalesOrderFromOpportunity step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IGenerateSalesOrderFromOpportunityPostContext<TEntity> : IStepRequestContext<GenerateSalesOrderFromOpportunityRequest>, IStepResponseContext<GenerateSalesOrderFromOpportunityResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a GenerateInvoiceFromOpportunity step (<c>ctx.Request</c> only).</summary>
    public interface IGenerateInvoiceFromOpportunityPreContext<TEntity> : IStepRequestContext<GenerateInvoiceFromOpportunityRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a GenerateInvoiceFromOpportunity step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IGenerateInvoiceFromOpportunityPostContext<TEntity> : IStepRequestContext<GenerateInvoiceFromOpportunityRequest>, IStepResponseContext<GenerateInvoiceFromOpportunityResponse> where TEntity : Entity { }
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
    /// <summary>Pre-stage typed context for a ReviseQuote step (<c>ctx.Request</c> only).</summary>
    public interface IReviseQuotePreContext<TEntity> : IStepRequestContext<ReviseQuoteRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a ReviseQuote step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IReviseQuotePostContext<TEntity> : IStepRequestContext<ReviseQuoteRequest>, IStepResponseContext<ReviseQuoteResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a FulfillSalesOrder step (<c>ctx.Request</c> only).</summary>
    public interface IFulfillSalesOrderPreContext<TEntity> : IStepRequestContext<FulfillSalesOrderRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a FulfillSalesOrder step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IFulfillSalesOrderPostContext<TEntity> : IStepRequestContext<FulfillSalesOrderRequest>, IStepResponseContext<FulfillSalesOrderResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a GetQuoteProductsFromOpportunity step (<c>ctx.Request</c> only).</summary>
    public interface IGetQuoteProductsFromOpportunityPreContext<TEntity> : IStepRequestContext<GetQuoteProductsFromOpportunityRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a GetQuoteProductsFromOpportunity step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IGetQuoteProductsFromOpportunityPostContext<TEntity> : IStepRequestContext<GetQuoteProductsFromOpportunityRequest>, IStepResponseContext<GetQuoteProductsFromOpportunityResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a GetSalesOrderProductsFromOpportunity step (<c>ctx.Request</c> only).</summary>
    public interface IGetSalesOrderProductsFromOpportunityPreContext<TEntity> : IStepRequestContext<GetSalesOrderProductsFromOpportunityRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a GetSalesOrderProductsFromOpportunity step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IGetSalesOrderProductsFromOpportunityPostContext<TEntity> : IStepRequestContext<GetSalesOrderProductsFromOpportunityRequest>, IStepResponseContext<GetSalesOrderProductsFromOpportunityResponse> where TEntity : Entity { }
    /// <summary>Pre-stage typed context for a GetInvoiceProductsFromOpportunity step (<c>ctx.Request</c> only).</summary>
    public interface IGetInvoiceProductsFromOpportunityPreContext<TEntity> : IStepRequestContext<GetInvoiceProductsFromOpportunityRequest> where TEntity : Entity { }
    /// <summary>Post-stage typed context for a GetInvoiceProductsFromOpportunity step (<c>ctx.Request</c> + <c>ctx.Response</c>).</summary>
    public interface IGetInvoiceProductsFromOpportunityPostContext<TEntity> : IStepRequestContext<GetInvoiceProductsFromOpportunityRequest>, IStepResponseContext<GetInvoiceProductsFromOpportunityResponse> where TEntity : Entity { }

    /// <summary>Runtime pre-stage context for Create.</summary>
    public sealed class CreatePreContext<TEntity> : PreStepContext<Vela.Messages.CreateRequest<TEntity>>, ICreatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Create.</summary>
    public sealed class CreatePostContext<TEntity> : PostStepContext<Vela.Messages.CreateRequest<TEntity>, CreateResponse>, ICreatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreatePostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PostImage => this.GetPostImage<TEntity>();
    }
    /// <summary>Runtime pre-stage context for Update.</summary>
    public sealed class UpdatePreContext<TEntity> : PreStepContext<Vela.Messages.UpdateRequest<TEntity>>, IUpdatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdatePreContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
    }
    /// <summary>Runtime post-stage context for Update.</summary>
    public sealed class UpdatePostContext<TEntity> : PostStepContext<Vela.Messages.UpdateRequest<TEntity>, UpdateResponse>, IUpdatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdatePostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
        /// <inheritdoc/>
        public TEntity? PostImage => this.GetPostImage<TEntity>();
    }
    /// <summary>Runtime pre-stage context for Delete.</summary>
    public sealed class DeletePreContext<TEntity> : PreStepContext<DeleteRequest>, IDeletePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeletePreContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
    }
    /// <summary>Runtime post-stage context for Delete.</summary>
    public sealed class DeletePostContext<TEntity> : PostStepContext<DeleteRequest, DeleteResponse>, IDeletePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeletePostContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity? PreImage => this.GetPreImage<TEntity>();
    }
    /// <summary>Runtime pre-stage context for CreateMultiple.</summary>
    public sealed class CreateMultiplePreContext<TEntity> : PreStepContext<CreateMultipleRequest>, ICreateMultiplePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreateMultiplePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for CreateMultiple.</summary>
    public sealed class CreateMultiplePostContext<TEntity> : PostStepContext<CreateMultipleRequest, CreateMultipleResponse>, ICreateMultiplePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreateMultiplePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for UpdateMultiple.</summary>
    public sealed class UpdateMultiplePreContext<TEntity> : PreStepContext<UpdateMultipleRequest>, IUpdateMultiplePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdateMultiplePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for UpdateMultiple.</summary>
    public sealed class UpdateMultiplePostContext<TEntity> : PostStepContext<UpdateMultipleRequest, UpdateMultipleResponse>, IUpdateMultiplePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdateMultiplePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Retrieve.</summary>
    public sealed class RetrievePreContext<TEntity> : PreStepContext<RetrieveRequest>, IRetrievePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrievePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Retrieve.</summary>
    public sealed class RetrievePostContext<TEntity> : PostStepContext<RetrieveRequest, RetrieveResponse>, IRetrievePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrievePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RetrieveMultiple.</summary>
    public sealed class RetrieveMultiplePreContext<TEntity> : PreStepContext<RetrieveMultipleRequest>, IRetrieveMultiplePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveMultiplePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RetrieveMultiple.</summary>
    public sealed class RetrieveMultiplePostContext<TEntity> : PostStepContext<RetrieveMultipleRequest, RetrieveMultipleResponse>, IRetrieveMultiplePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveMultiplePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Associate.</summary>
    public sealed class AssociatePreContext<TEntity> : PreStepContext<AssociateRequest>, IAssociatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AssociatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Associate.</summary>
    public sealed class AssociatePostContext<TEntity> : PostStepContext<AssociateRequest, AssociateResponse>, IAssociatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AssociatePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Disassociate.</summary>
    public sealed class DisassociatePreContext<TEntity> : PreStepContext<DisassociateRequest>, IDisassociatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DisassociatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Disassociate.</summary>
    public sealed class DisassociatePostContext<TEntity> : PostStepContext<DisassociateRequest, DisassociateResponse>, IDisassociatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DisassociatePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for Merge.</summary>
    public sealed class MergePreContext<TEntity> : PreStepContext<MergeRequest>, IMergePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MergePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for Merge.</summary>
    public sealed class MergePostContext<TEntity> : PostStepContext<MergeRequest, MergeResponse>, IMergePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MergePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for GrantAccess.</summary>
    public sealed class GrantAccessPreContext<TEntity> : PreStepContext<GrantAccessRequest>, IGrantAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GrantAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for GrantAccess.</summary>
    public sealed class GrantAccessPostContext<TEntity> : PostStepContext<GrantAccessRequest, GrantAccessResponse>, IGrantAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GrantAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for ModifyAccess.</summary>
    public sealed class ModifyAccessPreContext<TEntity> : PreStepContext<ModifyAccessRequest>, IModifyAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ModifyAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for ModifyAccess.</summary>
    public sealed class ModifyAccessPostContext<TEntity> : PostStepContext<ModifyAccessRequest, ModifyAccessResponse>, IModifyAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ModifyAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RevokeAccess.</summary>
    public sealed class RevokeAccessPreContext<TEntity> : PreStepContext<RevokeAccessRequest>, IRevokeAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RevokeAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RevokeAccess.</summary>
    public sealed class RevokeAccessPostContext<TEntity> : PostStepContext<RevokeAccessRequest, RevokeAccessResponse>, IRevokeAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RevokeAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for QualifyLead.</summary>
    public sealed class QualifyLeadPreContext<TEntity> : PreStepContext<QualifyLeadRequest>, IQualifyLeadPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public QualifyLeadPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for QualifyLead.</summary>
    public sealed class QualifyLeadPostContext<TEntity> : PostStepContext<QualifyLeadRequest, QualifyLeadResponse>, IQualifyLeadPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public QualifyLeadPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for WinOpportunity.</summary>
    public sealed class WinOpportunityPreContext<TEntity> : PreStepContext<WinOpportunityRequest>, IWinOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public WinOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for WinOpportunity.</summary>
    public sealed class WinOpportunityPostContext<TEntity> : PostStepContext<WinOpportunityRequest, WinOpportunityResponse>, IWinOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public WinOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for LoseOpportunity.</summary>
    public sealed class LoseOpportunityPreContext<TEntity> : PreStepContext<LoseOpportunityRequest>, ILoseOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public LoseOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for LoseOpportunity.</summary>
    public sealed class LoseOpportunityPostContext<TEntity> : PostStepContext<LoseOpportunityRequest, LoseOpportunityResponse>, ILoseOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public LoseOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for ConvertQuoteToSalesOrder.</summary>
    public sealed class ConvertQuoteToSalesOrderPreContext<TEntity> : PreStepContext<ConvertQuoteToSalesOrderRequest>, IConvertQuoteToSalesOrderPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ConvertQuoteToSalesOrderPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for ConvertQuoteToSalesOrder.</summary>
    public sealed class ConvertQuoteToSalesOrderPostContext<TEntity> : PostStepContext<ConvertQuoteToSalesOrderRequest, ConvertQuoteToSalesOrderResponse>, IConvertQuoteToSalesOrderPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ConvertQuoteToSalesOrderPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for ConvertSalesOrderToInvoice.</summary>
    public sealed class ConvertSalesOrderToInvoicePreContext<TEntity> : PreStepContext<ConvertSalesOrderToInvoiceRequest>, IConvertSalesOrderToInvoicePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ConvertSalesOrderToInvoicePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for ConvertSalesOrderToInvoice.</summary>
    public sealed class ConvertSalesOrderToInvoicePostContext<TEntity> : PostStepContext<ConvertSalesOrderToInvoiceRequest, ConvertSalesOrderToInvoiceResponse>, IConvertSalesOrderToInvoicePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ConvertSalesOrderToInvoicePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for GenerateQuoteFromOpportunity.</summary>
    public sealed class GenerateQuoteFromOpportunityPreContext<TEntity> : PreStepContext<GenerateQuoteFromOpportunityRequest>, IGenerateQuoteFromOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateQuoteFromOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for GenerateQuoteFromOpportunity.</summary>
    public sealed class GenerateQuoteFromOpportunityPostContext<TEntity> : PostStepContext<GenerateQuoteFromOpportunityRequest, GenerateQuoteFromOpportunityResponse>, IGenerateQuoteFromOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateQuoteFromOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for GenerateSalesOrderFromOpportunity.</summary>
    public sealed class GenerateSalesOrderFromOpportunityPreContext<TEntity> : PreStepContext<GenerateSalesOrderFromOpportunityRequest>, IGenerateSalesOrderFromOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateSalesOrderFromOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for GenerateSalesOrderFromOpportunity.</summary>
    public sealed class GenerateSalesOrderFromOpportunityPostContext<TEntity> : PostStepContext<GenerateSalesOrderFromOpportunityRequest, GenerateSalesOrderFromOpportunityResponse>, IGenerateSalesOrderFromOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateSalesOrderFromOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for GenerateInvoiceFromOpportunity.</summary>
    public sealed class GenerateInvoiceFromOpportunityPreContext<TEntity> : PreStepContext<GenerateInvoiceFromOpportunityRequest>, IGenerateInvoiceFromOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateInvoiceFromOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for GenerateInvoiceFromOpportunity.</summary>
    public sealed class GenerateInvoiceFromOpportunityPostContext<TEntity> : PostStepContext<GenerateInvoiceFromOpportunityRequest, GenerateInvoiceFromOpportunityResponse>, IGenerateInvoiceFromOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateInvoiceFromOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddToQueue.</summary>
    public sealed class AddToQueuePreContext<TEntity> : PreStepContext<AddToQueueRequest>, IAddToQueuePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddToQueuePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddToQueue.</summary>
    public sealed class AddToQueuePostContext<TEntity> : PostStepContext<AddToQueueRequest, AddToQueueResponse>, IAddToQueuePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddToQueuePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RemoveFromQueue.</summary>
    public sealed class RemoveFromQueuePreContext<TEntity> : PreStepContext<RemoveFromQueueRequest>, IRemoveFromQueuePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveFromQueuePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RemoveFromQueue.</summary>
    public sealed class RemoveFromQueuePostContext<TEntity> : PostStepContext<RemoveFromQueueRequest, RemoveFromQueueResponse>, IRemoveFromQueuePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveFromQueuePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for PickFromQueue.</summary>
    public sealed class PickFromQueuePreContext<TEntity> : PreStepContext<PickFromQueueRequest>, IPickFromQueuePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public PickFromQueuePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for PickFromQueue.</summary>
    public sealed class PickFromQueuePostContext<TEntity> : PostStepContext<PickFromQueueRequest, PickFromQueueResponse>, IPickFromQueuePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public PickFromQueuePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RouteTo.</summary>
    public sealed class RouteToPreContext<TEntity> : PreStepContext<RouteToRequest>, IRouteToPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RouteToPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RouteTo.</summary>
    public sealed class RouteToPostContext<TEntity> : PostStepContext<RouteToRequest, RouteToResponse>, IRouteToPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RouteToPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for ReleaseToQueue.</summary>
    public sealed class ReleaseToQueuePreContext<TEntity> : PreStepContext<ReleaseToQueueRequest>, IReleaseToQueuePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ReleaseToQueuePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for ReleaseToQueue.</summary>
    public sealed class ReleaseToQueuePostContext<TEntity> : PostStepContext<ReleaseToQueueRequest, ReleaseToQueueResponse>, IReleaseToQueuePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ReleaseToQueuePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for SendEmail.</summary>
    public sealed class SendEmailPreContext<TEntity> : PreStepContext<SendEmailRequest>, ISendEmailPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for SendEmail.</summary>
    public sealed class SendEmailPostContext<TEntity> : PostStepContext<SendEmailRequest, SendEmailResponse>, ISendEmailPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for SendEmailFromTemplate.</summary>
    public sealed class SendEmailFromTemplatePreContext<TEntity> : PreStepContext<Vela.Messages.SendEmailFromTemplateRequest<TEntity>>, ISendEmailFromTemplatePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailFromTemplatePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for SendEmailFromTemplate.</summary>
    public sealed class SendEmailFromTemplatePostContext<TEntity> : PostStepContext<Vela.Messages.SendEmailFromTemplateRequest<TEntity>, SendEmailFromTemplateResponse>, ISendEmailFromTemplatePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailFromTemplatePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for DeliverIncomingEmail.</summary>
    public sealed class DeliverIncomingEmailPreContext<TEntity> : PreStepContext<DeliverIncomingEmailRequest>, IDeliverIncomingEmailPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverIncomingEmailPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for DeliverIncomingEmail.</summary>
    public sealed class DeliverIncomingEmailPostContext<TEntity> : PostStepContext<DeliverIncomingEmailRequest, DeliverIncomingEmailResponse>, IDeliverIncomingEmailPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverIncomingEmailPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for DeliverPromoteEmail.</summary>
    public sealed class DeliverPromoteEmailPreContext<TEntity> : PreStepContext<DeliverPromoteEmailRequest>, IDeliverPromoteEmailPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverPromoteEmailPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for DeliverPromoteEmail.</summary>
    public sealed class DeliverPromoteEmailPostContext<TEntity> : PostStepContext<DeliverPromoteEmailRequest, DeliverPromoteEmailResponse>, IDeliverPromoteEmailPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverPromoteEmailPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddMembersTeam.</summary>
    public sealed class AddMembersTeamPreContext<TEntity> : PreStepContext<AddMembersTeamRequest>, IAddMembersTeamPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMembersTeamPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddMembersTeam.</summary>
    public sealed class AddMembersTeamPostContext<TEntity> : PostStepContext<AddMembersTeamRequest, AddMembersTeamResponse>, IAddMembersTeamPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMembersTeamPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RemoveMembersTeam.</summary>
    public sealed class RemoveMembersTeamPreContext<TEntity> : PreStepContext<RemoveMembersTeamRequest>, IRemoveMembersTeamPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMembersTeamPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RemoveMembersTeam.</summary>
    public sealed class RemoveMembersTeamPostContext<TEntity> : PostStepContext<RemoveMembersTeamRequest, RemoveMembersTeamResponse>, IRemoveMembersTeamPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMembersTeamPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddListMembersList.</summary>
    public sealed class AddListMembersListPreContext<TEntity> : PreStepContext<AddListMembersListRequest>, IAddListMembersListPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddListMembersListPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddListMembersList.</summary>
    public sealed class AddListMembersListPostContext<TEntity> : PostStepContext<AddListMembersListRequest, AddListMembersListResponse>, IAddListMembersListPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddListMembersListPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for AddMemberList.</summary>
    public sealed class AddMemberListPreContext<TEntity> : PreStepContext<AddMemberListRequest>, IAddMemberListPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMemberListPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for AddMemberList.</summary>
    public sealed class AddMemberListPostContext<TEntity> : PostStepContext<AddMemberListRequest, AddMemberListResponse>, IAddMemberListPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMemberListPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RemoveMemberList.</summary>
    public sealed class RemoveMemberListPreContext<TEntity> : PreStepContext<RemoveMemberListRequest>, IRemoveMemberListPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMemberListPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RemoveMemberList.</summary>
    public sealed class RemoveMemberListPostContext<TEntity> : PostStepContext<RemoveMemberListRequest, RemoveMemberListResponse>, IRemoveMemberListPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMemberListPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for CloseIncident.</summary>
    public sealed class CloseIncidentPreContext<TEntity> : PreStepContext<CloseIncidentRequest>, ICloseIncidentPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloseIncidentPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for CloseIncident.</summary>
    public sealed class CloseIncidentPostContext<TEntity> : PostStepContext<CloseIncidentRequest, CloseIncidentResponse>, ICloseIncidentPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloseIncidentPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for CancelContract.</summary>
    public sealed class CancelContractPreContext<TEntity> : PreStepContext<CancelContractRequest>, ICancelContractPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CancelContractPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for CancelContract.</summary>
    public sealed class CancelContractPostContext<TEntity> : PostStepContext<CancelContractRequest, CancelContractResponse>, ICancelContractPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CancelContractPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for CloneContract.</summary>
    public sealed class CloneContractPreContext<TEntity> : PreStepContext<CloneContractRequest>, ICloneContractPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloneContractPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for CloneContract.</summary>
    public sealed class CloneContractPostContext<TEntity> : PostStepContext<CloneContractRequest, CloneContractResponse>, ICloneContractPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloneContractPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RetrievePrincipalAccess.</summary>
    public sealed class RetrievePrincipalAccessPreContext<TEntity> : PreStepContext<RetrievePrincipalAccessRequest>, IRetrievePrincipalAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrievePrincipalAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RetrievePrincipalAccess.</summary>
    public sealed class RetrievePrincipalAccessPostContext<TEntity> : PostStepContext<RetrievePrincipalAccessRequest, RetrievePrincipalAccessResponse>, IRetrievePrincipalAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrievePrincipalAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for RetrieveSharedPrincipalsAndAccess.</summary>
    public sealed class RetrieveSharedPrincipalsAndAccessPreContext<TEntity> : PreStepContext<RetrieveSharedPrincipalsAndAccessRequest>, IRetrieveSharedPrincipalsAndAccessPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveSharedPrincipalsAndAccessPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for RetrieveSharedPrincipalsAndAccess.</summary>
    public sealed class RetrieveSharedPrincipalsAndAccessPostContext<TEntity> : PostStepContext<RetrieveSharedPrincipalsAndAccessRequest, RetrieveSharedPrincipalsAndAccessResponse>, IRetrieveSharedPrincipalsAndAccessPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveSharedPrincipalsAndAccessPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for WinQuote.</summary>
    public sealed class WinQuotePreContext<TEntity> : PreStepContext<WinQuoteRequest>, IWinQuotePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public WinQuotePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for WinQuote.</summary>
    public sealed class WinQuotePostContext<TEntity> : PostStepContext<WinQuoteRequest, WinQuoteResponse>, IWinQuotePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public WinQuotePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for ReviseQuote.</summary>
    public sealed class ReviseQuotePreContext<TEntity> : PreStepContext<ReviseQuoteRequest>, IReviseQuotePreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ReviseQuotePreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for ReviseQuote.</summary>
    public sealed class ReviseQuotePostContext<TEntity> : PostStepContext<ReviseQuoteRequest, ReviseQuoteResponse>, IReviseQuotePostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ReviseQuotePostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for FulfillSalesOrder.</summary>
    public sealed class FulfillSalesOrderPreContext<TEntity> : PreStepContext<FulfillSalesOrderRequest>, IFulfillSalesOrderPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public FulfillSalesOrderPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for FulfillSalesOrder.</summary>
    public sealed class FulfillSalesOrderPostContext<TEntity> : PostStepContext<FulfillSalesOrderRequest, FulfillSalesOrderResponse>, IFulfillSalesOrderPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public FulfillSalesOrderPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for GetQuoteProductsFromOpportunity.</summary>
    public sealed class GetQuoteProductsFromOpportunityPreContext<TEntity> : PreStepContext<GetQuoteProductsFromOpportunityRequest>, IGetQuoteProductsFromOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GetQuoteProductsFromOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for GetQuoteProductsFromOpportunity.</summary>
    public sealed class GetQuoteProductsFromOpportunityPostContext<TEntity> : PostStepContext<GetQuoteProductsFromOpportunityRequest, GetQuoteProductsFromOpportunityResponse>, IGetQuoteProductsFromOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GetQuoteProductsFromOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for GetSalesOrderProductsFromOpportunity.</summary>
    public sealed class GetSalesOrderProductsFromOpportunityPreContext<TEntity> : PreStepContext<GetSalesOrderProductsFromOpportunityRequest>, IGetSalesOrderProductsFromOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GetSalesOrderProductsFromOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for GetSalesOrderProductsFromOpportunity.</summary>
    public sealed class GetSalesOrderProductsFromOpportunityPostContext<TEntity> : PostStepContext<GetSalesOrderProductsFromOpportunityRequest, GetSalesOrderProductsFromOpportunityResponse>, IGetSalesOrderProductsFromOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GetSalesOrderProductsFromOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime pre-stage context for GetInvoiceProductsFromOpportunity.</summary>
    public sealed class GetInvoiceProductsFromOpportunityPreContext<TEntity> : PreStepContext<GetInvoiceProductsFromOpportunityRequest>, IGetInvoiceProductsFromOpportunityPreContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GetInvoiceProductsFromOpportunityPreContext(ILocalPluginContext inner) : base(inner) { }
    }
    /// <summary>Runtime post-stage context for GetInvoiceProductsFromOpportunity.</summary>
    public sealed class GetInvoiceProductsFromOpportunityPostContext<TEntity> : PostStepContext<GetInvoiceProductsFromOpportunityRequest, GetInvoiceProductsFromOpportunityResponse>, IGetInvoiceProductsFromOpportunityPostContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GetInvoiceProductsFromOpportunityPostContext(ILocalPluginContext inner) : base(inner) { }
    }
}
