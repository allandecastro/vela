using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Crm.Sdk.Messages;

namespace Vela.Context
{
    // Per-message typed contexts. Each is a two-line specialization of StepContext / IStepContext with the
    // message's SDK request/response types. TEntity ties the context to the step's entity (type-state), so a
    // handler declared for the wrong message does not compile. Adding a message = one interface + one class.
    //
    // On top of the standard SDK request/response (ctx.Request / ctx.Response), each context also exposes the
    // primary record already typed, so a handler never re-states the entity type or casts: ctx.Target (the
    // early-bound entity, Create/Update), ctx.TargetReference (the EntityReference, Delete/Retrieve/Associate/
    // Disassociate/Merge/*Access) and ctx.TargetCollection (the EntityCollection, CreateMultiple/UpdateMultiple).

    /// <summary>A context whose message carries a full entity <c>Target</c> (Create, Update).</summary>
    /// <typeparam name="TEntity">The step's entity type.</typeparam>
    public interface IEntityTargetContext<TEntity> where TEntity : Entity
    {
        /// <summary>The message <c>Target</c> as the early-bound entity (no cast, no type argument).</summary>
        TEntity Target { get; }
    }

    /// <summary>A context whose message carries an <c>EntityReference</c> Target (Delete, Retrieve, Associate,
    /// Disassociate, Merge, GrantAccess, ModifyAccess, RevokeAccess).</summary>
    public interface IReferenceTargetContext
    {
        /// <summary>The message <c>Target</c> as an <see cref="EntityReference"/>.</summary>
        EntityReference TargetReference { get; }
    }

    /// <summary>A bulk context whose message carries a <c>Targets</c> collection (CreateMultiple, UpdateMultiple).</summary>
    public interface ICollectionTargetContext
    {
        /// <summary>The message <c>Targets</c> as an <see cref="EntityCollection"/>.</summary>
        EntityCollection TargetCollection { get; }
    }

    /// <summary>Typed context for a Create step (<c>ctx.Target</c>, <c>ctx.Response.id</c>).</summary>
    public interface ICreateContext<TEntity> : IStepContext<CreateRequest, CreateResponse>, IEntityTargetContext<TEntity> where TEntity : Entity { }
    /// <summary>Typed context for an Update step (<c>ctx.Target</c>).</summary>
    public interface IUpdateContext<TEntity> : IStepContext<UpdateRequest, UpdateResponse>, IEntityTargetContext<TEntity> where TEntity : Entity { }
    /// <summary>Typed context for a Delete step (<c>ctx.TargetReference</c>).</summary>
    public interface IDeleteContext<TEntity> : IStepContext<DeleteRequest, DeleteResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a CreateMultiple step (<c>ctx.TargetCollection</c>, <c>ctx.Response.Ids</c>).</summary>
    public interface ICreateMultipleContext<TEntity> : IStepContext<CreateMultipleRequest, CreateMultipleResponse>, ICollectionTargetContext where TEntity : Entity { }
    /// <summary>Typed context for an UpdateMultiple step (<c>ctx.TargetCollection</c>).</summary>
    public interface IUpdateMultipleContext<TEntity> : IStepContext<UpdateMultipleRequest, UpdateMultipleResponse>, ICollectionTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a Retrieve step (<c>ctx.TargetReference</c>, <c>ctx.Request.ColumnSet</c>, <c>ctx.Response.Entity</c>).</summary>
    public interface IRetrieveContext<TEntity> : IStepContext<RetrieveRequest, RetrieveResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a RetrieveMultiple step (<c>ctx.Request.Query</c>).</summary>
    public interface IRetrieveMultipleContext<TEntity> : IStepContext<RetrieveMultipleRequest, RetrieveMultipleResponse> where TEntity : Entity { }
    /// <summary>Typed context for an Associate step (<c>ctx.TargetReference</c>, <c>ctx.Request.Relationship</c>, <c>RelatedEntities</c>).</summary>
    public interface IAssociateContext<TEntity> : IStepContext<AssociateRequest, AssociateResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a Disassociate step (<c>ctx.TargetReference</c>).</summary>
    public interface IDisassociateContext<TEntity> : IStepContext<DisassociateRequest, DisassociateResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a Merge step (<c>ctx.TargetReference</c>, <c>ctx.Request.SubordinateId</c>, <c>UpdateContent</c>…).</summary>
    public interface IMergeContext<TEntity> : IStepContext<MergeRequest, MergeResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a GrantAccess step (<c>ctx.TargetReference</c>, <c>ctx.Request.PrincipalAccess</c>).</summary>
    public interface IGrantAccessContext<TEntity> : IStepContext<GrantAccessRequest, GrantAccessResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a ModifyAccess step (<c>ctx.TargetReference</c>).</summary>
    public interface IModifyAccessContext<TEntity> : IStepContext<ModifyAccessRequest, ModifyAccessResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a RevokeAccess step (<c>ctx.TargetReference</c>, <c>ctx.Request.Revokee</c>).</summary>
    public interface IRevokeAccessContext<TEntity> : IStepContext<RevokeAccessRequest, RevokeAccessResponse>, IReferenceTargetContext where TEntity : Entity { }

    // Each class forwards its typed target accessor to the existing context extensions (GetTarget / GetTargetRef
    // / GetTargets), which read the pipeline InputParameters. The accessor is the only member beyond the ctor;
    // Request/Response come from StepContext.

    /// <summary>Runtime implementation of <see cref="ICreateContext{TEntity}"/>.</summary>
    public sealed class CreateContext<TEntity> : StepContext<CreateRequest, CreateResponse>, ICreateContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreateContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity Target => this.GetTarget<TEntity>();
    }

    /// <summary>Runtime implementation of <see cref="IUpdateContext{TEntity}"/>.</summary>
    public sealed class UpdateContext<TEntity> : StepContext<UpdateRequest, UpdateResponse>, IUpdateContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdateContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity Target => this.GetTarget<TEntity>();
    }

    /// <summary>Runtime implementation of <see cref="IDeleteContext{TEntity}"/>.</summary>
    public sealed class DeleteContext<TEntity> : StepContext<DeleteRequest, DeleteResponse>, IDeleteContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeleteContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="ICreateMultipleContext{TEntity}"/>.</summary>
    public sealed class CreateMultipleContext<TEntity> : StepContext<CreateMultipleRequest, CreateMultipleResponse>, ICreateMultipleContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreateMultipleContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityCollection TargetCollection => this.GetTargets();
    }

    /// <summary>Runtime implementation of <see cref="IUpdateMultipleContext{TEntity}"/>.</summary>
    public sealed class UpdateMultipleContext<TEntity> : StepContext<UpdateMultipleRequest, UpdateMultipleResponse>, IUpdateMultipleContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdateMultipleContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityCollection TargetCollection => this.GetTargets();
    }

    /// <summary>Runtime implementation of <see cref="IRetrieveContext{TEntity}"/>.</summary>
    public sealed class RetrieveContext<TEntity> : StepContext<RetrieveRequest, RetrieveResponse>, IRetrieveContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="IRetrieveMultipleContext{TEntity}"/>.</summary>
    public sealed class RetrieveMultipleContext<TEntity> : StepContext<RetrieveMultipleRequest, RetrieveMultipleResponse>, IRetrieveMultipleContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveMultipleContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IAssociateContext{TEntity}"/>.</summary>
    public sealed class AssociateContext<TEntity> : StepContext<AssociateRequest, AssociateResponse>, IAssociateContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AssociateContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="IDisassociateContext{TEntity}"/>.</summary>
    public sealed class DisassociateContext<TEntity> : StepContext<DisassociateRequest, DisassociateResponse>, IDisassociateContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DisassociateContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="IMergeContext{TEntity}"/>.</summary>
    public sealed class MergeContext<TEntity> : StepContext<MergeRequest, MergeResponse>, IMergeContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MergeContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="IGrantAccessContext{TEntity}"/>.</summary>
    public sealed class GrantAccessContext<TEntity> : StepContext<GrantAccessRequest, GrantAccessResponse>, IGrantAccessContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GrantAccessContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="IModifyAccessContext{TEntity}"/>.</summary>
    public sealed class ModifyAccessContext<TEntity> : StepContext<ModifyAccessRequest, ModifyAccessResponse>, IModifyAccessContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ModifyAccessContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="IRevokeAccessContext{TEntity}"/>.</summary>
    public sealed class RevokeAccessContext<TEntity> : StepContext<RevokeAccessRequest, RevokeAccessResponse>, IRevokeAccessContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RevokeAccessContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }
    /// <summary>Typed context for a QualifyLead step.</summary>
    public interface IQualifyLeadContext<TEntity> : IStepContext<QualifyLeadRequest, QualifyLeadResponse> where TEntity : Entity { }
    /// <summary>Typed context for a WinOpportunity step.</summary>
    public interface IWinOpportunityContext<TEntity> : IStepContext<WinOpportunityRequest, WinOpportunityResponse> where TEntity : Entity { }
    /// <summary>Typed context for a LoseOpportunity step.</summary>
    public interface ILoseOpportunityContext<TEntity> : IStepContext<LoseOpportunityRequest, LoseOpportunityResponse> where TEntity : Entity { }
    /// <summary>Typed context for a ConvertQuoteToSalesOrder step.</summary>
    public interface IConvertQuoteToSalesOrderContext<TEntity> : IStepContext<ConvertQuoteToSalesOrderRequest, ConvertQuoteToSalesOrderResponse> where TEntity : Entity { }
    /// <summary>Typed context for a ConvertSalesOrderToInvoice step.</summary>
    public interface IConvertSalesOrderToInvoiceContext<TEntity> : IStepContext<ConvertSalesOrderToInvoiceRequest, ConvertSalesOrderToInvoiceResponse> where TEntity : Entity { }
    /// <summary>Typed context for a GenerateQuoteFromOpportunity step.</summary>
    public interface IGenerateQuoteFromOpportunityContext<TEntity> : IStepContext<GenerateQuoteFromOpportunityRequest, GenerateQuoteFromOpportunityResponse> where TEntity : Entity { }
    /// <summary>Typed context for a GenerateSalesOrderFromOpportunity step.</summary>
    public interface IGenerateSalesOrderFromOpportunityContext<TEntity> : IStepContext<GenerateSalesOrderFromOpportunityRequest, GenerateSalesOrderFromOpportunityResponse> where TEntity : Entity { }
    /// <summary>Typed context for a GenerateInvoiceFromOpportunity step.</summary>
    public interface IGenerateInvoiceFromOpportunityContext<TEntity> : IStepContext<GenerateInvoiceFromOpportunityRequest, GenerateInvoiceFromOpportunityResponse> where TEntity : Entity { }
    /// <summary>Typed context for a AddToQueue step.</summary>
    public interface IAddToQueueContext<TEntity> : IStepContext<AddToQueueRequest, AddToQueueResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a RemoveFromQueue step.</summary>
    public interface IRemoveFromQueueContext<TEntity> : IStepContext<RemoveFromQueueRequest, RemoveFromQueueResponse> where TEntity : Entity { }
    /// <summary>Typed context for a PickFromQueue step.</summary>
    public interface IPickFromQueueContext<TEntity> : IStepContext<PickFromQueueRequest, PickFromQueueResponse> where TEntity : Entity { }
    /// <summary>Typed context for a RouteTo step.</summary>
    public interface IRouteToContext<TEntity> : IStepContext<RouteToRequest, RouteToResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a ReleaseToQueue step.</summary>
    public interface IReleaseToQueueContext<TEntity> : IStepContext<ReleaseToQueueRequest, ReleaseToQueueResponse> where TEntity : Entity { }
    /// <summary>Typed context for a SendEmail step.</summary>
    public interface ISendEmailContext<TEntity> : IStepContext<SendEmailRequest, SendEmailResponse> where TEntity : Entity { }
    /// <summary>Typed context for a SendEmailFromTemplate step.</summary>
    public interface ISendEmailFromTemplateContext<TEntity> : IStepContext<SendEmailFromTemplateRequest, SendEmailFromTemplateResponse>, IEntityTargetContext<TEntity> where TEntity : Entity { }
    /// <summary>Typed context for a DeliverIncomingEmail step.</summary>
    public interface IDeliverIncomingEmailContext<TEntity> : IStepContext<DeliverIncomingEmailRequest, DeliverIncomingEmailResponse> where TEntity : Entity { }
    /// <summary>Typed context for a DeliverPromoteEmail step.</summary>
    public interface IDeliverPromoteEmailContext<TEntity> : IStepContext<DeliverPromoteEmailRequest, DeliverPromoteEmailResponse> where TEntity : Entity { }
    /// <summary>Typed context for a AddMembersTeam step.</summary>
    public interface IAddMembersTeamContext<TEntity> : IStepContext<AddMembersTeamRequest, AddMembersTeamResponse> where TEntity : Entity { }
    /// <summary>Typed context for a RemoveMembersTeam step.</summary>
    public interface IRemoveMembersTeamContext<TEntity> : IStepContext<RemoveMembersTeamRequest, RemoveMembersTeamResponse> where TEntity : Entity { }
    /// <summary>Typed context for a AddListMembersList step.</summary>
    public interface IAddListMembersListContext<TEntity> : IStepContext<AddListMembersListRequest, AddListMembersListResponse> where TEntity : Entity { }
    /// <summary>Typed context for a AddMemberList step.</summary>
    public interface IAddMemberListContext<TEntity> : IStepContext<AddMemberListRequest, AddMemberListResponse> where TEntity : Entity { }
    /// <summary>Typed context for a RemoveMemberList step.</summary>
    public interface IRemoveMemberListContext<TEntity> : IStepContext<RemoveMemberListRequest, RemoveMemberListResponse> where TEntity : Entity { }
    /// <summary>Typed context for a CloseIncident step.</summary>
    public interface ICloseIncidentContext<TEntity> : IStepContext<CloseIncidentRequest, CloseIncidentResponse> where TEntity : Entity { }
    /// <summary>Typed context for a CancelContract step.</summary>
    public interface ICancelContractContext<TEntity> : IStepContext<CancelContractRequest, CancelContractResponse> where TEntity : Entity { }
    /// <summary>Typed context for a CloneContract step.</summary>
    public interface ICloneContractContext<TEntity> : IStepContext<CloneContractRequest, CloneContractResponse> where TEntity : Entity { }
    /// <summary>Typed context for a RetrievePrincipalAccess step.</summary>
    public interface IRetrievePrincipalAccessContext<TEntity> : IStepContext<RetrievePrincipalAccessRequest, RetrievePrincipalAccessResponse>, IReferenceTargetContext where TEntity : Entity { }
    /// <summary>Typed context for a RetrieveSharedPrincipalsAndAccess step.</summary>
    public interface IRetrieveSharedPrincipalsAndAccessContext<TEntity> : IStepContext<RetrieveSharedPrincipalsAndAccessRequest, RetrieveSharedPrincipalsAndAccessResponse>, IReferenceTargetContext where TEntity : Entity { }

    /// <summary>Runtime implementation of <see cref="IQualifyLeadContext{TEntity}"/>.</summary>
    public sealed class QualifyLeadContext<TEntity> : StepContext<QualifyLeadRequest, QualifyLeadResponse>, IQualifyLeadContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public QualifyLeadContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IWinOpportunityContext{TEntity}"/>.</summary>
    public sealed class WinOpportunityContext<TEntity> : StepContext<WinOpportunityRequest, WinOpportunityResponse>, IWinOpportunityContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public WinOpportunityContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="ILoseOpportunityContext{TEntity}"/>.</summary>
    public sealed class LoseOpportunityContext<TEntity> : StepContext<LoseOpportunityRequest, LoseOpportunityResponse>, ILoseOpportunityContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public LoseOpportunityContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IConvertQuoteToSalesOrderContext{TEntity}"/>.</summary>
    public sealed class ConvertQuoteToSalesOrderContext<TEntity> : StepContext<ConvertQuoteToSalesOrderRequest, ConvertQuoteToSalesOrderResponse>, IConvertQuoteToSalesOrderContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ConvertQuoteToSalesOrderContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IConvertSalesOrderToInvoiceContext{TEntity}"/>.</summary>
    public sealed class ConvertSalesOrderToInvoiceContext<TEntity> : StepContext<ConvertSalesOrderToInvoiceRequest, ConvertSalesOrderToInvoiceResponse>, IConvertSalesOrderToInvoiceContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ConvertSalesOrderToInvoiceContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IGenerateQuoteFromOpportunityContext{TEntity}"/>.</summary>
    public sealed class GenerateQuoteFromOpportunityContext<TEntity> : StepContext<GenerateQuoteFromOpportunityRequest, GenerateQuoteFromOpportunityResponse>, IGenerateQuoteFromOpportunityContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateQuoteFromOpportunityContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IGenerateSalesOrderFromOpportunityContext{TEntity}"/>.</summary>
    public sealed class GenerateSalesOrderFromOpportunityContext<TEntity> : StepContext<GenerateSalesOrderFromOpportunityRequest, GenerateSalesOrderFromOpportunityResponse>, IGenerateSalesOrderFromOpportunityContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateSalesOrderFromOpportunityContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IGenerateInvoiceFromOpportunityContext{TEntity}"/>.</summary>
    public sealed class GenerateInvoiceFromOpportunityContext<TEntity> : StepContext<GenerateInvoiceFromOpportunityRequest, GenerateInvoiceFromOpportunityResponse>, IGenerateInvoiceFromOpportunityContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GenerateInvoiceFromOpportunityContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IAddToQueueContext{TEntity}"/>.</summary>
    public sealed class AddToQueueContext<TEntity> : StepContext<AddToQueueRequest, AddToQueueResponse>, IAddToQueueContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddToQueueContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="IRemoveFromQueueContext{TEntity}"/>.</summary>
    public sealed class RemoveFromQueueContext<TEntity> : StepContext<RemoveFromQueueRequest, RemoveFromQueueResponse>, IRemoveFromQueueContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveFromQueueContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IPickFromQueueContext{TEntity}"/>.</summary>
    public sealed class PickFromQueueContext<TEntity> : StepContext<PickFromQueueRequest, PickFromQueueResponse>, IPickFromQueueContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public PickFromQueueContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IRouteToContext{TEntity}"/>.</summary>
    public sealed class RouteToContext<TEntity> : StepContext<RouteToRequest, RouteToResponse>, IRouteToContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RouteToContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="IReleaseToQueueContext{TEntity}"/>.</summary>
    public sealed class ReleaseToQueueContext<TEntity> : StepContext<ReleaseToQueueRequest, ReleaseToQueueResponse>, IReleaseToQueueContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ReleaseToQueueContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="ISendEmailContext{TEntity}"/>.</summary>
    public sealed class SendEmailContext<TEntity> : StepContext<SendEmailRequest, SendEmailResponse>, ISendEmailContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="ISendEmailFromTemplateContext{TEntity}"/>.</summary>
    public sealed class SendEmailFromTemplateContext<TEntity> : StepContext<SendEmailFromTemplateRequest, SendEmailFromTemplateResponse>, ISendEmailFromTemplateContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public SendEmailFromTemplateContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public TEntity Target => this.GetTarget<TEntity>();
    }

    /// <summary>Runtime implementation of <see cref="IDeliverIncomingEmailContext{TEntity}"/>.</summary>
    public sealed class DeliverIncomingEmailContext<TEntity> : StepContext<DeliverIncomingEmailRequest, DeliverIncomingEmailResponse>, IDeliverIncomingEmailContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverIncomingEmailContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IDeliverPromoteEmailContext{TEntity}"/>.</summary>
    public sealed class DeliverPromoteEmailContext<TEntity> : StepContext<DeliverPromoteEmailRequest, DeliverPromoteEmailResponse>, IDeliverPromoteEmailContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeliverPromoteEmailContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IAddMembersTeamContext{TEntity}"/>.</summary>
    public sealed class AddMembersTeamContext<TEntity> : StepContext<AddMembersTeamRequest, AddMembersTeamResponse>, IAddMembersTeamContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMembersTeamContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IRemoveMembersTeamContext{TEntity}"/>.</summary>
    public sealed class RemoveMembersTeamContext<TEntity> : StepContext<RemoveMembersTeamRequest, RemoveMembersTeamResponse>, IRemoveMembersTeamContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMembersTeamContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IAddListMembersListContext{TEntity}"/>.</summary>
    public sealed class AddListMembersListContext<TEntity> : StepContext<AddListMembersListRequest, AddListMembersListResponse>, IAddListMembersListContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddListMembersListContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IAddMemberListContext{TEntity}"/>.</summary>
    public sealed class AddMemberListContext<TEntity> : StepContext<AddMemberListRequest, AddMemberListResponse>, IAddMemberListContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AddMemberListContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IRemoveMemberListContext{TEntity}"/>.</summary>
    public sealed class RemoveMemberListContext<TEntity> : StepContext<RemoveMemberListRequest, RemoveMemberListResponse>, IRemoveMemberListContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RemoveMemberListContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="ICloseIncidentContext{TEntity}"/>.</summary>
    public sealed class CloseIncidentContext<TEntity> : StepContext<CloseIncidentRequest, CloseIncidentResponse>, ICloseIncidentContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloseIncidentContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="ICancelContractContext{TEntity}"/>.</summary>
    public sealed class CancelContractContext<TEntity> : StepContext<CancelContractRequest, CancelContractResponse>, ICancelContractContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CancelContractContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="ICloneContractContext{TEntity}"/>.</summary>
    public sealed class CloneContractContext<TEntity> : StepContext<CloneContractRequest, CloneContractResponse>, ICloneContractContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CloneContractContext(ILocalPluginContext inner) : base(inner) { }
    }

    /// <summary>Runtime implementation of <see cref="IRetrievePrincipalAccessContext{TEntity}"/>.</summary>
    public sealed class RetrievePrincipalAccessContext<TEntity> : StepContext<RetrievePrincipalAccessRequest, RetrievePrincipalAccessResponse>, IRetrievePrincipalAccessContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrievePrincipalAccessContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

    /// <summary>Runtime implementation of <see cref="IRetrieveSharedPrincipalsAndAccessContext{TEntity}"/>.</summary>
    public sealed class RetrieveSharedPrincipalsAndAccessContext<TEntity> : StepContext<RetrieveSharedPrincipalsAndAccessRequest, RetrieveSharedPrincipalsAndAccessResponse>, IRetrieveSharedPrincipalsAndAccessContext<TEntity> where TEntity : Entity
    {
        /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveSharedPrincipalsAndAccessContext(ILocalPluginContext inner) : base(inner) { }
        /// <inheritdoc/>
        public EntityReference TargetReference => this.GetTargetRef();
    }

}
