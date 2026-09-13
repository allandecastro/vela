using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Entry point of the typed step chain: choose the message. The message determines which stage selector is
/// returned, and the stage in turn determines which entity images the step may capture - an invalid
/// image/stage/message combination does not compile.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IStepBuilder<TEntity> where TEntity : Entity
{
    /// <summary>Configure step to trigger on Create message. Filtering attributes are required.</summary>
    ICreateStepStage<TEntity> OnCreate();

    /// <summary>Configure step to trigger on Update message. Filtering attributes are required.</summary>
    IUpdateStepStage<TEntity> OnUpdate();

    /// <summary>
    /// Configure step to trigger on the CreateMultiple (bulk create) message. The step fires once with the
    /// whole <c>Targets</c> collection (read it via <c>ctx.BulkTargets</c>). Filtering attributes are required.
    /// </summary>
    ICreateMultipleStepStage<TEntity> OnCreateMultiple();

    /// <summary>
    /// Configure step to trigger on the UpdateMultiple (bulk update) message. The step fires once with the
    /// whole <c>Targets</c> collection (read it via <c>ctx.BulkTargets</c>). Filtering attributes are required.
    /// </summary>
    IUpdateMultipleStepStage<TEntity> OnUpdateMultiple();

    /// <summary>Configure step to trigger on Delete message.</summary>
    IDeleteStepStage<TEntity> OnDelete();

    /// <summary>Configure step to trigger on Retrieve message.</summary>
    IRetrieveStepStage<TEntity> OnRetrieve();

    /// <summary>Configure step to trigger on RetrieveMultiple message.</summary>
    IRetrieveMultipleStepStage<TEntity> OnRetrieveMultiple();

    /// <summary>Configure step to trigger on Associate message.</summary>
    IAssociateStepStage<TEntity> OnAssociate();

    /// <summary>Configure step to trigger on Disassociate message.</summary>
    IDisassociateStepStage<TEntity> OnDisassociate();

    /// <summary>Configure step to trigger on the Merge message (handler receives an <c>IMergeContext</c>).</summary>
    IMergeStepStage<TEntity> OnMerge();

    /// <summary>Configure step to trigger on the GrantAccess message.</summary>
    IGrantAccessStepStage<TEntity> OnGrantAccess();

    /// <summary>Configure step to trigger on the ModifyAccess message.</summary>
    IModifyAccessStepStage<TEntity> OnModifyAccess();

    /// <summary>Configure step to trigger on the RevokeAccess message.</summary>
    IRevokeAccessStepStage<TEntity> OnRevokeAccess();

    /// <summary>Begin a QualifyLead step.</summary>
    IQualifyLeadStepStage<TEntity> OnQualifyLead();
    /// <summary>Begin a WinOpportunity step.</summary>
    IWinOpportunityStepStage<TEntity> OnWinOpportunity();
    /// <summary>Begin a LoseOpportunity step.</summary>
    ILoseOpportunityStepStage<TEntity> OnLoseOpportunity();
    /// <summary>Begin a ConvertQuoteToSalesOrder step.</summary>
    IConvertQuoteToSalesOrderStepStage<TEntity> OnConvertQuoteToSalesOrder();
    /// <summary>Begin a ConvertSalesOrderToInvoice step.</summary>
    IConvertSalesOrderToInvoiceStepStage<TEntity> OnConvertSalesOrderToInvoice();
    /// <summary>Begin a GenerateQuoteFromOpportunity step.</summary>
    IGenerateQuoteFromOpportunityStepStage<TEntity> OnGenerateQuoteFromOpportunity();
    /// <summary>Begin a GenerateSalesOrderFromOpportunity step.</summary>
    IGenerateSalesOrderFromOpportunityStepStage<TEntity> OnGenerateSalesOrderFromOpportunity();
    /// <summary>Begin a GenerateInvoiceFromOpportunity step.</summary>
    IGenerateInvoiceFromOpportunityStepStage<TEntity> OnGenerateInvoiceFromOpportunity();
    /// <summary>Begin a AddToQueue step.</summary>
    IAddToQueueStepStage<TEntity> OnAddToQueue();
    /// <summary>Begin a RemoveFromQueue step.</summary>
    IRemoveFromQueueStepStage<TEntity> OnRemoveFromQueue();
    /// <summary>Begin a PickFromQueue step.</summary>
    IPickFromQueueStepStage<TEntity> OnPickFromQueue();
    /// <summary>Begin a RouteTo step.</summary>
    IRouteToStepStage<TEntity> OnRouteTo();
    /// <summary>Begin a ReleaseToQueue step.</summary>
    IReleaseToQueueStepStage<TEntity> OnReleaseToQueue();
    /// <summary>Begin a SendEmail step.</summary>
    ISendEmailStepStage<TEntity> OnSendEmail();
    /// <summary>Begin a SendEmailFromTemplate step.</summary>
    ISendEmailFromTemplateStepStage<TEntity> OnSendEmailFromTemplate();
    /// <summary>Begin a DeliverIncomingEmail step.</summary>
    IDeliverIncomingEmailStepStage<TEntity> OnDeliverIncomingEmail();
    /// <summary>Begin a DeliverPromoteEmail step.</summary>
    IDeliverPromoteEmailStepStage<TEntity> OnDeliverPromoteEmail();
    /// <summary>Begin a AddMembersTeam step.</summary>
    IAddMembersTeamStepStage<TEntity> OnAddMembersTeam();
    /// <summary>Begin a RemoveMembersTeam step.</summary>
    IRemoveMembersTeamStepStage<TEntity> OnRemoveMembersTeam();
    /// <summary>Begin a AddListMembersList step.</summary>
    IAddListMembersListStepStage<TEntity> OnAddListMembersList();
    /// <summary>Begin a AddMemberList step.</summary>
    IAddMemberListStepStage<TEntity> OnAddMemberList();
    /// <summary>Begin a RemoveMemberList step.</summary>
    IRemoveMemberListStepStage<TEntity> OnRemoveMemberList();
    /// <summary>Begin a CloseIncident step.</summary>
    ICloseIncidentStepStage<TEntity> OnCloseIncident();
    /// <summary>Begin a CancelContract step.</summary>
    ICancelContractStepStage<TEntity> OnCancelContract();
    /// <summary>Begin a CloneContract step.</summary>
    ICloneContractStepStage<TEntity> OnCloneContract();
    /// <summary>Begin a RetrievePrincipalAccess step.</summary>
    IRetrievePrincipalAccessStepStage<TEntity> OnRetrievePrincipalAccess();
    /// <summary>Begin a RetrieveSharedPrincipalsAndAccess step.</summary>
    IRetrieveSharedPrincipalsAndAccessStepStage<TEntity> OnRetrieveSharedPrincipalsAndAccess();
    /// <summary>Begin a WhoAmI step.</summary>
    IWhoAmIStepStage<TEntity> OnWhoAmI();
    /// <summary>Begin a WinQuote step.</summary>
    IWinQuoteStepStage<TEntity> OnWinQuote();
    /// <summary>Begin a ReviseQuote step.</summary>
    IReviseQuoteStepStage<TEntity> OnReviseQuote();
    /// <summary>Begin a FulfillSalesOrder step.</summary>
    IFulfillSalesOrderStepStage<TEntity> OnFulfillSalesOrder();
    /// <summary>Begin a GetQuoteProductsFromOpportunity step.</summary>
    IGetQuoteProductsFromOpportunityStepStage<TEntity> OnGetQuoteProductsFromOpportunity();
    /// <summary>Begin a GetSalesOrderProductsFromOpportunity step.</summary>
    IGetSalesOrderProductsFromOpportunityStepStage<TEntity> OnGetSalesOrderProductsFromOpportunity();
    /// <summary>Begin a GetInvoiceProductsFromOpportunity step.</summary>
    IGetInvoiceProductsFromOpportunityStepStage<TEntity> OnGetInvoiceProductsFromOpportunity();

    /// <summary>Configure step to trigger on a custom message. Image validity is enforced at runtime.</summary>
    /// <param name="messageName">The SDK message name.</param>
    IPermissiveStepStage<TEntity> OnMessage(string messageName);

    /// <summary>Configure step to trigger on a typed custom message. Image validity is enforced at runtime.</summary>
    /// <typeparam name="TMessage">The message marker type.</typeparam>
    IPermissiveStepStage<TEntity> OnMessage<TMessage>() where TMessage : class;

    /// <summary>
    /// Configure step to trigger on the message named by <typeparamref name="TRequest"/> (its
    /// <c>RequestName</c>), stating the request type once. The handler bound at the end of the chain is pre-typed:
    /// <c>ctx.Request</c> is <c>IMessagePre/PostContext&lt;TRequest&gt;</c> without restating the type on
    /// <c>Handler</c>. Image validity is enforced at runtime (permissive, like <see cref="OnMessage(string)"/>).
    /// </summary>
    /// <typeparam name="TRequest">A request type (an SDK request, a generated Custom API request, or a
    /// <c>TypedRequest</c>) with a public parameterless constructor.</typeparam>
    ITypedMessageStepStage<TEntity, TRequest> On<TRequest>() where TRequest : OrganizationRequest, new();

    /// <summary>
    /// Like <see cref="On{TRequest}"/> but returns an async-only stage (no <c>.Sync()</c>): the entry point the
    /// generated <c>On&lt;Message&gt;()</c> extension of a Custom API whose <c>allowedcustomprocessingsteptype</c>
    /// is <c>AsyncOnly</c> delegates to (Phase 3c). Not intended to be called by hand - the mode restriction is a
    /// property of the Custom API, discovered by <c>vela models --custom-apis</c>, not a per-step choice.
    /// </summary>
    /// <typeparam name="TRequest">A request type with a public parameterless constructor.</typeparam>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    IAsyncOnlyTypedMessageStepStage<TEntity, TRequest> OnAsyncOnly<TRequest>() where TRequest : OrganizationRequest, new();
}
