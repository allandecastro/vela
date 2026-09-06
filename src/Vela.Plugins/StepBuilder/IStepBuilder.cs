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

    /// <summary>Configure step to trigger on a custom message. Image validity is enforced at runtime.</summary>
    /// <param name="messageName">The SDK message name.</param>
    IPermissiveStepStage<TEntity> OnMessage(string messageName);

    /// <summary>Configure step to trigger on a typed custom message. Image validity is enforced at runtime.</summary>
    /// <typeparam name="TMessage">The message marker type.</typeparam>
    IPermissiveStepStage<TEntity> OnMessage<TMessage>() where TMessage : class;
}
