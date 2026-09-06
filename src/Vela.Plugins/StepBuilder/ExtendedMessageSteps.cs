using System;
using System.Collections.Generic;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

// Per-message typed steps for additional modelled SDK messages (sales lifecycle, queues, email, list/team
// membership, case/contract, access reads). Each is a no-image step: I{Msg}Step adds the message-typed
// .Handler; settings are inherited from the shared INoImageStep terminal.

/// <summary>QualifyLead step: bind <c>.Handler(OnQualifyLead)</c> where the handler takes an <c>IQualifyLeadContext</c>.</summary>
public interface IQualifyLeadStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IQualifyLeadContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IQualifyLeadContext<TEntity>> handler);
}

/// <summary>Stage selection for QualifyLead.</summary>
public interface IQualifyLeadStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IQualifyLeadStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IQualifyLeadStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IQualifyLeadStep<TEntity>> PostOperation();
}

/// <summary>WinOpportunity step: bind <c>.Handler(OnWinOpportunity)</c> where the handler takes an <c>IWinOpportunityContext</c>.</summary>
public interface IWinOpportunityStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IWinOpportunityContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IWinOpportunityContext<TEntity>> handler);
}

/// <summary>Stage selection for WinOpportunity.</summary>
public interface IWinOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IWinOpportunityStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IWinOpportunityStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IWinOpportunityStep<TEntity>> PostOperation();
}

/// <summary>LoseOpportunity step: bind <c>.Handler(OnLoseOpportunity)</c> where the handler takes an <c>ILoseOpportunityContext</c>.</summary>
public interface ILoseOpportunityStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(ILoseOpportunityContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ILoseOpportunityContext<TEntity>> handler);
}

/// <summary>Stage selection for LoseOpportunity.</summary>
public interface ILoseOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ILoseOpportunityStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ILoseOpportunityStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ILoseOpportunityStep<TEntity>> PostOperation();
}

/// <summary>ConvertQuoteToSalesOrder step: bind <c>.Handler(OnConvertQuoteToSalesOrder)</c> where the handler takes an <c>IConvertQuoteToSalesOrderContext</c>.</summary>
public interface IConvertQuoteToSalesOrderStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IConvertQuoteToSalesOrderContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IConvertQuoteToSalesOrderContext<TEntity>> handler);
}

/// <summary>Stage selection for ConvertQuoteToSalesOrder.</summary>
public interface IConvertQuoteToSalesOrderStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IConvertQuoteToSalesOrderStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IConvertQuoteToSalesOrderStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IConvertQuoteToSalesOrderStep<TEntity>> PostOperation();
}

/// <summary>ConvertSalesOrderToInvoice step: bind <c>.Handler(OnConvertSalesOrderToInvoice)</c> where the handler takes an <c>IConvertSalesOrderToInvoiceContext</c>.</summary>
public interface IConvertSalesOrderToInvoiceStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IConvertSalesOrderToInvoiceContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IConvertSalesOrderToInvoiceContext<TEntity>> handler);
}

/// <summary>Stage selection for ConvertSalesOrderToInvoice.</summary>
public interface IConvertSalesOrderToInvoiceStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IConvertSalesOrderToInvoiceStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IConvertSalesOrderToInvoiceStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IConvertSalesOrderToInvoiceStep<TEntity>> PostOperation();
}

/// <summary>GenerateQuoteFromOpportunity step: bind <c>.Handler(OnGenerateQuoteFromOpportunity)</c> where the handler takes an <c>IGenerateQuoteFromOpportunityContext</c>.</summary>
public interface IGenerateQuoteFromOpportunityStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IGenerateQuoteFromOpportunityContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGenerateQuoteFromOpportunityContext<TEntity>> handler);
}

/// <summary>Stage selection for GenerateQuoteFromOpportunity.</summary>
public interface IGenerateQuoteFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGenerateQuoteFromOpportunityStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGenerateQuoteFromOpportunityStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGenerateQuoteFromOpportunityStep<TEntity>> PostOperation();
}

/// <summary>GenerateSalesOrderFromOpportunity step: bind <c>.Handler(OnGenerateSalesOrderFromOpportunity)</c> where the handler takes an <c>IGenerateSalesOrderFromOpportunityContext</c>.</summary>
public interface IGenerateSalesOrderFromOpportunityStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IGenerateSalesOrderFromOpportunityContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGenerateSalesOrderFromOpportunityContext<TEntity>> handler);
}

/// <summary>Stage selection for GenerateSalesOrderFromOpportunity.</summary>
public interface IGenerateSalesOrderFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGenerateSalesOrderFromOpportunityStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGenerateSalesOrderFromOpportunityStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGenerateSalesOrderFromOpportunityStep<TEntity>> PostOperation();
}

/// <summary>GenerateInvoiceFromOpportunity step: bind <c>.Handler(OnGenerateInvoiceFromOpportunity)</c> where the handler takes an <c>IGenerateInvoiceFromOpportunityContext</c>.</summary>
public interface IGenerateInvoiceFromOpportunityStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IGenerateInvoiceFromOpportunityContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGenerateInvoiceFromOpportunityContext<TEntity>> handler);
}

/// <summary>Stage selection for GenerateInvoiceFromOpportunity.</summary>
public interface IGenerateInvoiceFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGenerateInvoiceFromOpportunityStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGenerateInvoiceFromOpportunityStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGenerateInvoiceFromOpportunityStep<TEntity>> PostOperation();
}

/// <summary>AddToQueue step: bind <c>.Handler(OnAddToQueue)</c> where the handler takes an <c>IAddToQueueContext</c>.</summary>
public interface IAddToQueueStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IAddToQueueContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddToQueueContext<TEntity>> handler);
}

/// <summary>Stage selection for AddToQueue.</summary>
public interface IAddToQueueStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IAddToQueueStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IAddToQueueStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IAddToQueueStep<TEntity>> PostOperation();
}

/// <summary>RemoveFromQueue step: bind <c>.Handler(OnRemoveFromQueue)</c> where the handler takes an <c>IRemoveFromQueueContext</c>.</summary>
public interface IRemoveFromQueueStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IRemoveFromQueueContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRemoveFromQueueContext<TEntity>> handler);
}

/// <summary>Stage selection for RemoveFromQueue.</summary>
public interface IRemoveFromQueueStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRemoveFromQueueStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRemoveFromQueueStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRemoveFromQueueStep<TEntity>> PostOperation();
}

/// <summary>PickFromQueue step: bind <c>.Handler(OnPickFromQueue)</c> where the handler takes an <c>IPickFromQueueContext</c>.</summary>
public interface IPickFromQueueStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IPickFromQueueContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IPickFromQueueContext<TEntity>> handler);
}

/// <summary>Stage selection for PickFromQueue.</summary>
public interface IPickFromQueueStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IPickFromQueueStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IPickFromQueueStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IPickFromQueueStep<TEntity>> PostOperation();
}

/// <summary>RouteTo step: bind <c>.Handler(OnRouteTo)</c> where the handler takes an <c>IRouteToContext</c>.</summary>
public interface IRouteToStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IRouteToContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRouteToContext<TEntity>> handler);
}

/// <summary>Stage selection for RouteTo.</summary>
public interface IRouteToStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRouteToStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRouteToStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRouteToStep<TEntity>> PostOperation();
}

/// <summary>ReleaseToQueue step: bind <c>.Handler(OnReleaseToQueue)</c> where the handler takes an <c>IReleaseToQueueContext</c>.</summary>
public interface IReleaseToQueueStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IReleaseToQueueContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IReleaseToQueueContext<TEntity>> handler);
}

/// <summary>Stage selection for ReleaseToQueue.</summary>
public interface IReleaseToQueueStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IReleaseToQueueStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IReleaseToQueueStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IReleaseToQueueStep<TEntity>> PostOperation();
}

/// <summary>SendEmail step: bind <c>.Handler(OnSendEmail)</c> where the handler takes an <c>ISendEmailContext</c>.</summary>
public interface ISendEmailStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(ISendEmailContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ISendEmailContext<TEntity>> handler);
}

/// <summary>Stage selection for SendEmail.</summary>
public interface ISendEmailStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ISendEmailStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ISendEmailStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ISendEmailStep<TEntity>> PostOperation();
}

/// <summary>SendEmailFromTemplate step: bind <c>.Handler(OnSendEmailFromTemplate)</c> where the handler takes an <c>ISendEmailFromTemplateContext</c>.</summary>
public interface ISendEmailFromTemplateStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(ISendEmailFromTemplateContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ISendEmailFromTemplateContext<TEntity>> handler);
}

/// <summary>Stage selection for SendEmailFromTemplate.</summary>
public interface ISendEmailFromTemplateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ISendEmailFromTemplateStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ISendEmailFromTemplateStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ISendEmailFromTemplateStep<TEntity>> PostOperation();
}

/// <summary>DeliverIncomingEmail step: bind <c>.Handler(OnDeliverIncomingEmail)</c> where the handler takes an <c>IDeliverIncomingEmailContext</c>.</summary>
public interface IDeliverIncomingEmailStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IDeliverIncomingEmailContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDeliverIncomingEmailContext<TEntity>> handler);
}

/// <summary>Stage selection for DeliverIncomingEmail.</summary>
public interface IDeliverIncomingEmailStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IDeliverIncomingEmailStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IDeliverIncomingEmailStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IDeliverIncomingEmailStep<TEntity>> PostOperation();
}

/// <summary>DeliverPromoteEmail step: bind <c>.Handler(OnDeliverPromoteEmail)</c> where the handler takes an <c>IDeliverPromoteEmailContext</c>.</summary>
public interface IDeliverPromoteEmailStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IDeliverPromoteEmailContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDeliverPromoteEmailContext<TEntity>> handler);
}

/// <summary>Stage selection for DeliverPromoteEmail.</summary>
public interface IDeliverPromoteEmailStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IDeliverPromoteEmailStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IDeliverPromoteEmailStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IDeliverPromoteEmailStep<TEntity>> PostOperation();
}

/// <summary>AddMembersTeam step: bind <c>.Handler(OnAddMembersTeam)</c> where the handler takes an <c>IAddMembersTeamContext</c>.</summary>
public interface IAddMembersTeamStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IAddMembersTeamContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddMembersTeamContext<TEntity>> handler);
}

/// <summary>Stage selection for AddMembersTeam.</summary>
public interface IAddMembersTeamStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IAddMembersTeamStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IAddMembersTeamStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IAddMembersTeamStep<TEntity>> PostOperation();
}

/// <summary>RemoveMembersTeam step: bind <c>.Handler(OnRemoveMembersTeam)</c> where the handler takes an <c>IRemoveMembersTeamContext</c>.</summary>
public interface IRemoveMembersTeamStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IRemoveMembersTeamContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRemoveMembersTeamContext<TEntity>> handler);
}

/// <summary>Stage selection for RemoveMembersTeam.</summary>
public interface IRemoveMembersTeamStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRemoveMembersTeamStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRemoveMembersTeamStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRemoveMembersTeamStep<TEntity>> PostOperation();
}

/// <summary>AddListMembersList step: bind <c>.Handler(OnAddListMembersList)</c> where the handler takes an <c>IAddListMembersListContext</c>.</summary>
public interface IAddListMembersListStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IAddListMembersListContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddListMembersListContext<TEntity>> handler);
}

/// <summary>Stage selection for AddListMembersList.</summary>
public interface IAddListMembersListStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IAddListMembersListStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IAddListMembersListStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IAddListMembersListStep<TEntity>> PostOperation();
}

/// <summary>AddMemberList step: bind <c>.Handler(OnAddMemberList)</c> where the handler takes an <c>IAddMemberListContext</c>.</summary>
public interface IAddMemberListStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IAddMemberListContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddMemberListContext<TEntity>> handler);
}

/// <summary>Stage selection for AddMemberList.</summary>
public interface IAddMemberListStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IAddMemberListStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IAddMemberListStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IAddMemberListStep<TEntity>> PostOperation();
}

/// <summary>RemoveMemberList step: bind <c>.Handler(OnRemoveMemberList)</c> where the handler takes an <c>IRemoveMemberListContext</c>.</summary>
public interface IRemoveMemberListStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IRemoveMemberListContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRemoveMemberListContext<TEntity>> handler);
}

/// <summary>Stage selection for RemoveMemberList.</summary>
public interface IRemoveMemberListStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRemoveMemberListStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRemoveMemberListStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRemoveMemberListStep<TEntity>> PostOperation();
}

/// <summary>CloseIncident step: bind <c>.Handler(OnCloseIncident)</c> where the handler takes an <c>ICloseIncidentContext</c>.</summary>
public interface ICloseIncidentStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(ICloseIncidentContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICloseIncidentContext<TEntity>> handler);
}

/// <summary>Stage selection for CloseIncident.</summary>
public interface ICloseIncidentStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ICloseIncidentStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ICloseIncidentStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ICloseIncidentStep<TEntity>> PostOperation();
}

/// <summary>CancelContract step: bind <c>.Handler(OnCancelContract)</c> where the handler takes an <c>ICancelContractContext</c>.</summary>
public interface ICancelContractStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(ICancelContractContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICancelContractContext<TEntity>> handler);
}

/// <summary>Stage selection for CancelContract.</summary>
public interface ICancelContractStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ICancelContractStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ICancelContractStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ICancelContractStep<TEntity>> PostOperation();
}

/// <summary>CloneContract step: bind <c>.Handler(OnCloneContract)</c> where the handler takes an <c>ICloneContractContext</c>.</summary>
public interface ICloneContractStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(ICloneContractContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICloneContractContext<TEntity>> handler);
}

/// <summary>Stage selection for CloneContract.</summary>
public interface ICloneContractStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ICloneContractStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ICloneContractStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ICloneContractStep<TEntity>> PostOperation();
}

/// <summary>RetrievePrincipalAccess step: bind <c>.Handler(OnRetrievePrincipalAccess)</c> where the handler takes an <c>IRetrievePrincipalAccessContext</c>.</summary>
public interface IRetrievePrincipalAccessStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrievePrincipalAccessContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrievePrincipalAccessContext<TEntity>> handler);
}

/// <summary>Stage selection for RetrievePrincipalAccess.</summary>
public interface IRetrievePrincipalAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRetrievePrincipalAccessStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRetrievePrincipalAccessStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRetrievePrincipalAccessStep<TEntity>> PostOperation();
}

/// <summary>RetrieveSharedPrincipalsAndAccess step: bind <c>.Handler(OnRetrieveSharedPrincipalsAndAccess)</c> where the handler takes an <c>IRetrieveSharedPrincipalsAndAccessContext</c>.</summary>
public interface IRetrieveSharedPrincipalsAndAccessStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrieveSharedPrincipalsAndAccessContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrieveSharedPrincipalsAndAccessContext<TEntity>> handler);
}

/// <summary>Stage selection for RetrieveSharedPrincipalsAndAccess.</summary>
public interface IRetrieveSharedPrincipalsAndAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRetrieveSharedPrincipalsAndAccessStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRetrieveSharedPrincipalsAndAccessStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRetrieveSharedPrincipalsAndAccessStep<TEntity>> PostOperation();
}

/// <summary>WhoAmI step: bind <c>.Handler(OnWhoAmI)</c> where the handler takes an <c>IWhoAmIContext</c>.</summary>
public interface IWhoAmIStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IWhoAmIContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IWhoAmIContext<TEntity>> handler);
}

/// <summary>Stage selection for WhoAmI.</summary>
public interface IWhoAmIStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IWhoAmIStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IWhoAmIStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IWhoAmIStep<TEntity>> PostOperation();
}

/// <summary>WinQuote step: bind <c>.Handler(OnWinQuote)</c> where the handler takes an <c>IWinQuoteContext</c>.</summary>
public interface IWinQuoteStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IWinQuoteContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IWinQuoteContext<TEntity>> handler);
}

/// <summary>Stage selection for WinQuote.</summary>
public interface IWinQuoteStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IWinQuoteStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IWinQuoteStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IWinQuoteStep<TEntity>> PostOperation();
}

/// <summary>ReviseQuote step: bind <c>.Handler(OnReviseQuote)</c> where the handler takes an <c>IReviseQuoteContext</c>.</summary>
public interface IReviseQuoteStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IReviseQuoteContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IReviseQuoteContext<TEntity>> handler);
}

/// <summary>Stage selection for ReviseQuote.</summary>
public interface IReviseQuoteStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IReviseQuoteStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IReviseQuoteStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IReviseQuoteStep<TEntity>> PostOperation();
}

/// <summary>FulfillSalesOrder step: bind <c>.Handler(OnFulfillSalesOrder)</c> where the handler takes an <c>IFulfillSalesOrderContext</c>.</summary>
public interface IFulfillSalesOrderStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IFulfillSalesOrderContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IFulfillSalesOrderContext<TEntity>> handler);
}

/// <summary>Stage selection for FulfillSalesOrder.</summary>
public interface IFulfillSalesOrderStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IFulfillSalesOrderStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IFulfillSalesOrderStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IFulfillSalesOrderStep<TEntity>> PostOperation();
}

/// <summary>GetQuoteProductsFromOpportunity step: bind <c>.Handler(OnGetQuoteProductsFromOpportunity)</c> where the handler takes an <c>IGetQuoteProductsFromOpportunityContext</c>.</summary>
public interface IGetQuoteProductsFromOpportunityStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IGetQuoteProductsFromOpportunityContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGetQuoteProductsFromOpportunityContext<TEntity>> handler);
}

/// <summary>Stage selection for GetQuoteProductsFromOpportunity.</summary>
public interface IGetQuoteProductsFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGetQuoteProductsFromOpportunityStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGetQuoteProductsFromOpportunityStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGetQuoteProductsFromOpportunityStep<TEntity>> PostOperation();
}

/// <summary>GetSalesOrderProductsFromOpportunity step: bind <c>.Handler(OnGetSalesOrderProductsFromOpportunity)</c> where the handler takes an <c>IGetSalesOrderProductsFromOpportunityContext</c>.</summary>
public interface IGetSalesOrderProductsFromOpportunityStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IGetSalesOrderProductsFromOpportunityContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGetSalesOrderProductsFromOpportunityContext<TEntity>> handler);
}

/// <summary>Stage selection for GetSalesOrderProductsFromOpportunity.</summary>
public interface IGetSalesOrderProductsFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGetSalesOrderProductsFromOpportunityStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGetSalesOrderProductsFromOpportunityStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGetSalesOrderProductsFromOpportunityStep<TEntity>> PostOperation();
}

/// <summary>GetInvoiceProductsFromOpportunity step: bind <c>.Handler(OnGetInvoiceProductsFromOpportunity)</c> where the handler takes an <c>IGetInvoiceProductsFromOpportunityContext</c>.</summary>
public interface IGetInvoiceProductsFromOpportunityStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group).</summary>
    /// <param name="handler">Instance method with signature <c>(IGetInvoiceProductsFromOpportunityContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGetInvoiceProductsFromOpportunityContext<TEntity>> handler);
}

/// <summary>Stage selection for GetInvoiceProductsFromOpportunity.</summary>
public interface IGetInvoiceProductsFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGetInvoiceProductsFromOpportunityStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGetInvoiceProductsFromOpportunityStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGetInvoiceProductsFromOpportunityStep<TEntity>> PostOperation();
}

