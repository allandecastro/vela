using System;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

// Per-message step interfaces, split by pipeline stage so the handler receives the stage-correct context:
// a pre-stage step binds Action<I{Msg}PreContext> (Request only); a post-stage step binds
// Action<I{Msg}PostContext> (Request + Response). Image capability comes from the shared terminal each
// step extends (INoImageStep / IPreImageStep / IPostImageStep / IPreAndPostImageStep).

/// <summary>Create step (pre-stage).</summary>
public interface ICreatePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCreate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICreatePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICreatePreContext<TEntity>> handler);
}

/// <summary>Create step (post-stage).</summary>
public interface ICreatePostStep<TEntity> : IPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCreate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICreatePostContext&lt;TEntity&gt;)</c>.</param>
    IPostImageStep<TEntity> Handler(Action<ICreatePostContext<TEntity>> handler);
}

/// <summary>Stage selection for Create.</summary>
public interface ICreateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreatePreStep<TEntity>>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreatePreStep<TEntity>>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreatePostStep<TEntity>>> PostOperation();
}

/// <summary>Update step (pre-stage).</summary>
public interface IUpdatePreValStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnUpdate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IUpdatePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IUpdatePreContext<TEntity>> handler);
}

/// <summary>Update step (pre-stage).</summary>
public interface IUpdatePreOpStep<TEntity> : IPreImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnUpdate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IUpdatePreContext&lt;TEntity&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler(Action<IUpdatePreContext<TEntity>> handler);
}

/// <summary>Update step (post-stage).</summary>
public interface IUpdatePostStep<TEntity> : IPreAndPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnUpdate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IUpdatePostContext&lt;TEntity&gt;)</c>.</param>
    IPreAndPostImageStep<TEntity> Handler(Action<IUpdatePostContext<TEntity>> handler);
}

/// <summary>Stage selection for Update.</summary>
public interface IUpdateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreValStep<TEntity>>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdatePreOpStep<TEntity>>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdatePostStep<TEntity>>> PostOperation();
}

/// <summary>Delete step (pre-stage).</summary>
public interface IDeletePreValStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDelete)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDeletePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDeletePreContext<TEntity>> handler);
}

/// <summary>Delete step (pre-stage).</summary>
public interface IDeletePreOpStep<TEntity> : IPreImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDelete)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDeletePreContext&lt;TEntity&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler(Action<IDeletePreContext<TEntity>> handler);
}

/// <summary>Delete step (post-stage).</summary>
public interface IDeletePostStep<TEntity> : IPreImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDelete)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDeletePostContext&lt;TEntity&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler(Action<IDeletePostContext<TEntity>> handler);
}

/// <summary>Stage selection for Delete.</summary>
public interface IDeleteStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IDeletePreValStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IDeletePreOpStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IDeletePostStep<TEntity>> PostOperation();
}

/// <summary>CreateMultiple step (pre-stage).</summary>
public interface ICreateMultiplePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCreateMultiple)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICreateMultiplePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICreateMultiplePreContext<TEntity>> handler);
}

/// <summary>CreateMultiple step (post-stage).</summary>
public interface ICreateMultiplePostStep<TEntity> : IPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCreateMultiple)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICreateMultiplePostContext&lt;TEntity&gt;)</c>.</param>
    IPostImageStep<TEntity> Handler(Action<ICreateMultiplePostContext<TEntity>> handler);
}

/// <summary>Stage selection for CreateMultiple.</summary>
public interface ICreateMultipleStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePreStep<TEntity>>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePreStep<TEntity>>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IFilteringSelector<TEntity, ICreateMultiplePostStep<TEntity>>> PostOperation();
}

/// <summary>UpdateMultiple step (pre-stage).</summary>
public interface IUpdateMultiplePreValStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnUpdateMultiple)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IUpdateMultiplePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IUpdateMultiplePreContext<TEntity>> handler);
}

/// <summary>UpdateMultiple step (pre-stage).</summary>
public interface IUpdateMultiplePreOpStep<TEntity> : IPreImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnUpdateMultiple)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IUpdateMultiplePreContext&lt;TEntity&gt;)</c>.</param>
    IPreImageStep<TEntity> Handler(Action<IUpdateMultiplePreContext<TEntity>> handler);
}

/// <summary>UpdateMultiple step (post-stage).</summary>
public interface IUpdateMultiplePostStep<TEntity> : IPreAndPostImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnUpdateMultiple)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IUpdateMultiplePostContext&lt;TEntity&gt;)</c>.</param>
    IPreAndPostImageStep<TEntity> Handler(Action<IUpdateMultiplePostContext<TEntity>> handler);
}

/// <summary>Stage selection for UpdateMultiple.</summary>
public interface IUpdateMultipleStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreValStep<TEntity>>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePreOpStep<TEntity>>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IFilteringSelector<TEntity, IUpdateMultiplePostStep<TEntity>>> PostOperation();
}

/// <summary>Retrieve step (pre-stage).</summary>
public interface IRetrievePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRetrieve)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrievePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrievePreContext<TEntity>> handler);
}

/// <summary>Retrieve step (post-stage).</summary>
public interface IRetrievePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRetrieve)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrievePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrievePostContext<TEntity>> handler);
}

/// <summary>Stage selection for Retrieve.</summary>
public interface IRetrieveStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRetrievePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRetrievePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRetrievePostStep<TEntity>> PostOperation();
}

/// <summary>RetrieveMultiple step (pre-stage).</summary>
public interface IRetrieveMultiplePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRetrieveMultiple)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrieveMultiplePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrieveMultiplePreContext<TEntity>> handler);
}

/// <summary>RetrieveMultiple step (post-stage).</summary>
public interface IRetrieveMultiplePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRetrieveMultiple)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrieveMultiplePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrieveMultiplePostContext<TEntity>> handler);
}

/// <summary>Stage selection for RetrieveMultiple.</summary>
public interface IRetrieveMultipleStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRetrieveMultiplePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRetrieveMultiplePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRetrieveMultiplePostStep<TEntity>> PostOperation();
}

/// <summary>Associate step (pre-stage).</summary>
public interface IAssociatePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAssociate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAssociatePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAssociatePreContext<TEntity>> handler);
}

/// <summary>Associate step (post-stage).</summary>
public interface IAssociatePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAssociate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAssociatePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAssociatePostContext<TEntity>> handler);
}

/// <summary>Stage selection for Associate.</summary>
public interface IAssociateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IAssociatePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IAssociatePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IAssociatePostStep<TEntity>> PostOperation();
}

/// <summary>Disassociate step (pre-stage).</summary>
public interface IDisassociatePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDisassociate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDisassociatePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDisassociatePreContext<TEntity>> handler);
}

/// <summary>Disassociate step (post-stage).</summary>
public interface IDisassociatePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDisassociate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDisassociatePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDisassociatePostContext<TEntity>> handler);
}

/// <summary>Stage selection for Disassociate.</summary>
public interface IDisassociateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IDisassociatePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IDisassociatePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IDisassociatePostStep<TEntity>> PostOperation();
}

/// <summary>Merge step (pre-stage).</summary>
public interface IMergePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnMerge)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IMergePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IMergePreContext<TEntity>> handler);
}

/// <summary>Merge step (post-stage).</summary>
public interface IMergePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnMerge)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IMergePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IMergePostContext<TEntity>> handler);
}

/// <summary>Stage selection for Merge.</summary>
public interface IMergeStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IMergePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IMergePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IMergePostStep<TEntity>> PostOperation();
}

/// <summary>GrantAccess step (pre-stage).</summary>
public interface IGrantAccessPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGrantAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGrantAccessPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGrantAccessPreContext<TEntity>> handler);
}

/// <summary>GrantAccess step (post-stage).</summary>
public interface IGrantAccessPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGrantAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGrantAccessPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGrantAccessPostContext<TEntity>> handler);
}

/// <summary>Stage selection for GrantAccess.</summary>
public interface IGrantAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGrantAccessPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGrantAccessPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGrantAccessPostStep<TEntity>> PostOperation();
}

/// <summary>ModifyAccess step (pre-stage).</summary>
public interface IModifyAccessPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnModifyAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IModifyAccessPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IModifyAccessPreContext<TEntity>> handler);
}

/// <summary>ModifyAccess step (post-stage).</summary>
public interface IModifyAccessPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnModifyAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IModifyAccessPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IModifyAccessPostContext<TEntity>> handler);
}

/// <summary>Stage selection for ModifyAccess.</summary>
public interface IModifyAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IModifyAccessPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IModifyAccessPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IModifyAccessPostStep<TEntity>> PostOperation();
}

/// <summary>RevokeAccess step (pre-stage).</summary>
public interface IRevokeAccessPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRevokeAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRevokeAccessPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRevokeAccessPreContext<TEntity>> handler);
}

/// <summary>RevokeAccess step (post-stage).</summary>
public interface IRevokeAccessPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRevokeAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRevokeAccessPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRevokeAccessPostContext<TEntity>> handler);
}

/// <summary>Stage selection for RevokeAccess.</summary>
public interface IRevokeAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRevokeAccessPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRevokeAccessPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRevokeAccessPostStep<TEntity>> PostOperation();
}

/// <summary>QualifyLead step (pre-stage).</summary>
public interface IQualifyLeadPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnQualifyLead)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IQualifyLeadPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IQualifyLeadPreContext<TEntity>> handler);
}

/// <summary>QualifyLead step (post-stage).</summary>
public interface IQualifyLeadPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnQualifyLead)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IQualifyLeadPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IQualifyLeadPostContext<TEntity>> handler);
}

/// <summary>Stage selection for QualifyLead.</summary>
public interface IQualifyLeadStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IQualifyLeadPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IQualifyLeadPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IQualifyLeadPostStep<TEntity>> PostOperation();
}

/// <summary>WinOpportunity step (pre-stage).</summary>
public interface IWinOpportunityPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnWinOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IWinOpportunityPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IWinOpportunityPreContext<TEntity>> handler);
}

/// <summary>WinOpportunity step (post-stage).</summary>
public interface IWinOpportunityPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnWinOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IWinOpportunityPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IWinOpportunityPostContext<TEntity>> handler);
}

/// <summary>Stage selection for WinOpportunity.</summary>
public interface IWinOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IWinOpportunityPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IWinOpportunityPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IWinOpportunityPostStep<TEntity>> PostOperation();
}

/// <summary>LoseOpportunity step (pre-stage).</summary>
public interface ILoseOpportunityPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnLoseOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ILoseOpportunityPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ILoseOpportunityPreContext<TEntity>> handler);
}

/// <summary>LoseOpportunity step (post-stage).</summary>
public interface ILoseOpportunityPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnLoseOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ILoseOpportunityPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ILoseOpportunityPostContext<TEntity>> handler);
}

/// <summary>Stage selection for LoseOpportunity.</summary>
public interface ILoseOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ILoseOpportunityPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ILoseOpportunityPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ILoseOpportunityPostStep<TEntity>> PostOperation();
}

/// <summary>ConvertQuoteToSalesOrder step (pre-stage).</summary>
public interface IConvertQuoteToSalesOrderPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnConvertQuoteToSalesOrder)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IConvertQuoteToSalesOrderPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IConvertQuoteToSalesOrderPreContext<TEntity>> handler);
}

/// <summary>ConvertQuoteToSalesOrder step (post-stage).</summary>
public interface IConvertQuoteToSalesOrderPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnConvertQuoteToSalesOrder)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IConvertQuoteToSalesOrderPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IConvertQuoteToSalesOrderPostContext<TEntity>> handler);
}

/// <summary>Stage selection for ConvertQuoteToSalesOrder.</summary>
public interface IConvertQuoteToSalesOrderStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IConvertQuoteToSalesOrderPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IConvertQuoteToSalesOrderPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IConvertQuoteToSalesOrderPostStep<TEntity>> PostOperation();
}

/// <summary>ConvertSalesOrderToInvoice step (pre-stage).</summary>
public interface IConvertSalesOrderToInvoicePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnConvertSalesOrderToInvoice)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IConvertSalesOrderToInvoicePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IConvertSalesOrderToInvoicePreContext<TEntity>> handler);
}

/// <summary>ConvertSalesOrderToInvoice step (post-stage).</summary>
public interface IConvertSalesOrderToInvoicePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnConvertSalesOrderToInvoice)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IConvertSalesOrderToInvoicePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IConvertSalesOrderToInvoicePostContext<TEntity>> handler);
}

/// <summary>Stage selection for ConvertSalesOrderToInvoice.</summary>
public interface IConvertSalesOrderToInvoiceStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IConvertSalesOrderToInvoicePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IConvertSalesOrderToInvoicePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IConvertSalesOrderToInvoicePostStep<TEntity>> PostOperation();
}

/// <summary>GenerateQuoteFromOpportunity step (pre-stage).</summary>
public interface IGenerateQuoteFromOpportunityPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGenerateQuoteFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGenerateQuoteFromOpportunityPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGenerateQuoteFromOpportunityPreContext<TEntity>> handler);
}

/// <summary>GenerateQuoteFromOpportunity step (post-stage).</summary>
public interface IGenerateQuoteFromOpportunityPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGenerateQuoteFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGenerateQuoteFromOpportunityPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGenerateQuoteFromOpportunityPostContext<TEntity>> handler);
}

/// <summary>Stage selection for GenerateQuoteFromOpportunity.</summary>
public interface IGenerateQuoteFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGenerateQuoteFromOpportunityPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGenerateQuoteFromOpportunityPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGenerateQuoteFromOpportunityPostStep<TEntity>> PostOperation();
}

/// <summary>GenerateSalesOrderFromOpportunity step (pre-stage).</summary>
public interface IGenerateSalesOrderFromOpportunityPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGenerateSalesOrderFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGenerateSalesOrderFromOpportunityPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGenerateSalesOrderFromOpportunityPreContext<TEntity>> handler);
}

/// <summary>GenerateSalesOrderFromOpportunity step (post-stage).</summary>
public interface IGenerateSalesOrderFromOpportunityPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGenerateSalesOrderFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGenerateSalesOrderFromOpportunityPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGenerateSalesOrderFromOpportunityPostContext<TEntity>> handler);
}

/// <summary>Stage selection for GenerateSalesOrderFromOpportunity.</summary>
public interface IGenerateSalesOrderFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGenerateSalesOrderFromOpportunityPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGenerateSalesOrderFromOpportunityPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGenerateSalesOrderFromOpportunityPostStep<TEntity>> PostOperation();
}

/// <summary>GenerateInvoiceFromOpportunity step (pre-stage).</summary>
public interface IGenerateInvoiceFromOpportunityPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGenerateInvoiceFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGenerateInvoiceFromOpportunityPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGenerateInvoiceFromOpportunityPreContext<TEntity>> handler);
}

/// <summary>GenerateInvoiceFromOpportunity step (post-stage).</summary>
public interface IGenerateInvoiceFromOpportunityPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGenerateInvoiceFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGenerateInvoiceFromOpportunityPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGenerateInvoiceFromOpportunityPostContext<TEntity>> handler);
}

/// <summary>Stage selection for GenerateInvoiceFromOpportunity.</summary>
public interface IGenerateInvoiceFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGenerateInvoiceFromOpportunityPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGenerateInvoiceFromOpportunityPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGenerateInvoiceFromOpportunityPostStep<TEntity>> PostOperation();
}

/// <summary>AddToQueue step (pre-stage).</summary>
public interface IAddToQueuePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAddToQueue)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAddToQueuePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddToQueuePreContext<TEntity>> handler);
}

/// <summary>AddToQueue step (post-stage).</summary>
public interface IAddToQueuePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAddToQueue)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAddToQueuePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddToQueuePostContext<TEntity>> handler);
}

/// <summary>Stage selection for AddToQueue.</summary>
public interface IAddToQueueStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IAddToQueuePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IAddToQueuePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IAddToQueuePostStep<TEntity>> PostOperation();
}

/// <summary>RemoveFromQueue step (pre-stage).</summary>
public interface IRemoveFromQueuePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRemoveFromQueue)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRemoveFromQueuePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRemoveFromQueuePreContext<TEntity>> handler);
}

/// <summary>RemoveFromQueue step (post-stage).</summary>
public interface IRemoveFromQueuePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRemoveFromQueue)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRemoveFromQueuePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRemoveFromQueuePostContext<TEntity>> handler);
}

/// <summary>Stage selection for RemoveFromQueue.</summary>
public interface IRemoveFromQueueStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRemoveFromQueuePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRemoveFromQueuePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRemoveFromQueuePostStep<TEntity>> PostOperation();
}

/// <summary>PickFromQueue step (pre-stage).</summary>
public interface IPickFromQueuePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnPickFromQueue)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IPickFromQueuePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IPickFromQueuePreContext<TEntity>> handler);
}

/// <summary>PickFromQueue step (post-stage).</summary>
public interface IPickFromQueuePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnPickFromQueue)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IPickFromQueuePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IPickFromQueuePostContext<TEntity>> handler);
}

/// <summary>Stage selection for PickFromQueue.</summary>
public interface IPickFromQueueStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IPickFromQueuePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IPickFromQueuePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IPickFromQueuePostStep<TEntity>> PostOperation();
}

/// <summary>RouteTo step (pre-stage).</summary>
public interface IRouteToPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRouteTo)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRouteToPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRouteToPreContext<TEntity>> handler);
}

/// <summary>RouteTo step (post-stage).</summary>
public interface IRouteToPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRouteTo)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRouteToPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRouteToPostContext<TEntity>> handler);
}

/// <summary>Stage selection for RouteTo.</summary>
public interface IRouteToStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRouteToPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRouteToPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRouteToPostStep<TEntity>> PostOperation();
}

/// <summary>ReleaseToQueue step (pre-stage).</summary>
public interface IReleaseToQueuePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnReleaseToQueue)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IReleaseToQueuePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IReleaseToQueuePreContext<TEntity>> handler);
}

/// <summary>ReleaseToQueue step (post-stage).</summary>
public interface IReleaseToQueuePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnReleaseToQueue)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IReleaseToQueuePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IReleaseToQueuePostContext<TEntity>> handler);
}

/// <summary>Stage selection for ReleaseToQueue.</summary>
public interface IReleaseToQueueStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IReleaseToQueuePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IReleaseToQueuePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IReleaseToQueuePostStep<TEntity>> PostOperation();
}

/// <summary>SendEmail step (pre-stage).</summary>
public interface ISendEmailPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnSendEmail)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ISendEmailPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ISendEmailPreContext<TEntity>> handler);
}

/// <summary>SendEmail step (post-stage).</summary>
public interface ISendEmailPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnSendEmail)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ISendEmailPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ISendEmailPostContext<TEntity>> handler);
}

/// <summary>Stage selection for SendEmail.</summary>
public interface ISendEmailStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ISendEmailPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ISendEmailPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ISendEmailPostStep<TEntity>> PostOperation();
}

/// <summary>SendEmailFromTemplate step (pre-stage).</summary>
public interface ISendEmailFromTemplatePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnSendEmailFromTemplate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ISendEmailFromTemplatePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ISendEmailFromTemplatePreContext<TEntity>> handler);
}

/// <summary>SendEmailFromTemplate step (post-stage).</summary>
public interface ISendEmailFromTemplatePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnSendEmailFromTemplate)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ISendEmailFromTemplatePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ISendEmailFromTemplatePostContext<TEntity>> handler);
}

/// <summary>Stage selection for SendEmailFromTemplate.</summary>
public interface ISendEmailFromTemplateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ISendEmailFromTemplatePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ISendEmailFromTemplatePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ISendEmailFromTemplatePostStep<TEntity>> PostOperation();
}

/// <summary>DeliverIncomingEmail step (pre-stage).</summary>
public interface IDeliverIncomingEmailPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDeliverIncomingEmail)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDeliverIncomingEmailPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDeliverIncomingEmailPreContext<TEntity>> handler);
}

/// <summary>DeliverIncomingEmail step (post-stage).</summary>
public interface IDeliverIncomingEmailPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDeliverIncomingEmail)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDeliverIncomingEmailPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDeliverIncomingEmailPostContext<TEntity>> handler);
}

/// <summary>Stage selection for DeliverIncomingEmail.</summary>
public interface IDeliverIncomingEmailStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IDeliverIncomingEmailPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IDeliverIncomingEmailPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IDeliverIncomingEmailPostStep<TEntity>> PostOperation();
}

/// <summary>DeliverPromoteEmail step (pre-stage).</summary>
public interface IDeliverPromoteEmailPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDeliverPromoteEmail)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDeliverPromoteEmailPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDeliverPromoteEmailPreContext<TEntity>> handler);
}

/// <summary>DeliverPromoteEmail step (post-stage).</summary>
public interface IDeliverPromoteEmailPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnDeliverPromoteEmail)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IDeliverPromoteEmailPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDeliverPromoteEmailPostContext<TEntity>> handler);
}

/// <summary>Stage selection for DeliverPromoteEmail.</summary>
public interface IDeliverPromoteEmailStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IDeliverPromoteEmailPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IDeliverPromoteEmailPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IDeliverPromoteEmailPostStep<TEntity>> PostOperation();
}

/// <summary>AddMembersTeam step (pre-stage).</summary>
public interface IAddMembersTeamPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAddMembersTeam)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAddMembersTeamPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddMembersTeamPreContext<TEntity>> handler);
}

/// <summary>AddMembersTeam step (post-stage).</summary>
public interface IAddMembersTeamPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAddMembersTeam)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAddMembersTeamPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddMembersTeamPostContext<TEntity>> handler);
}

/// <summary>Stage selection for AddMembersTeam.</summary>
public interface IAddMembersTeamStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IAddMembersTeamPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IAddMembersTeamPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IAddMembersTeamPostStep<TEntity>> PostOperation();
}

/// <summary>RemoveMembersTeam step (pre-stage).</summary>
public interface IRemoveMembersTeamPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRemoveMembersTeam)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRemoveMembersTeamPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRemoveMembersTeamPreContext<TEntity>> handler);
}

/// <summary>RemoveMembersTeam step (post-stage).</summary>
public interface IRemoveMembersTeamPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRemoveMembersTeam)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRemoveMembersTeamPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRemoveMembersTeamPostContext<TEntity>> handler);
}

/// <summary>Stage selection for RemoveMembersTeam.</summary>
public interface IRemoveMembersTeamStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRemoveMembersTeamPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRemoveMembersTeamPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRemoveMembersTeamPostStep<TEntity>> PostOperation();
}

/// <summary>AddListMembersList step (pre-stage).</summary>
public interface IAddListMembersListPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAddListMembersList)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAddListMembersListPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddListMembersListPreContext<TEntity>> handler);
}

/// <summary>AddListMembersList step (post-stage).</summary>
public interface IAddListMembersListPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAddListMembersList)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAddListMembersListPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddListMembersListPostContext<TEntity>> handler);
}

/// <summary>Stage selection for AddListMembersList.</summary>
public interface IAddListMembersListStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IAddListMembersListPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IAddListMembersListPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IAddListMembersListPostStep<TEntity>> PostOperation();
}

/// <summary>AddMemberList step (pre-stage).</summary>
public interface IAddMemberListPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAddMemberList)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAddMemberListPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddMemberListPreContext<TEntity>> handler);
}

/// <summary>AddMemberList step (post-stage).</summary>
public interface IAddMemberListPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnAddMemberList)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IAddMemberListPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAddMemberListPostContext<TEntity>> handler);
}

/// <summary>Stage selection for AddMemberList.</summary>
public interface IAddMemberListStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IAddMemberListPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IAddMemberListPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IAddMemberListPostStep<TEntity>> PostOperation();
}

/// <summary>RemoveMemberList step (pre-stage).</summary>
public interface IRemoveMemberListPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRemoveMemberList)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRemoveMemberListPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRemoveMemberListPreContext<TEntity>> handler);
}

/// <summary>RemoveMemberList step (post-stage).</summary>
public interface IRemoveMemberListPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRemoveMemberList)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRemoveMemberListPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRemoveMemberListPostContext<TEntity>> handler);
}

/// <summary>Stage selection for RemoveMemberList.</summary>
public interface IRemoveMemberListStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRemoveMemberListPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRemoveMemberListPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRemoveMemberListPostStep<TEntity>> PostOperation();
}

/// <summary>CloseIncident step (pre-stage).</summary>
public interface ICloseIncidentPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCloseIncident)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICloseIncidentPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICloseIncidentPreContext<TEntity>> handler);
}

/// <summary>CloseIncident step (post-stage).</summary>
public interface ICloseIncidentPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCloseIncident)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICloseIncidentPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICloseIncidentPostContext<TEntity>> handler);
}

/// <summary>Stage selection for CloseIncident.</summary>
public interface ICloseIncidentStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ICloseIncidentPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ICloseIncidentPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ICloseIncidentPostStep<TEntity>> PostOperation();
}

/// <summary>CancelContract step (pre-stage).</summary>
public interface ICancelContractPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCancelContract)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICancelContractPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICancelContractPreContext<TEntity>> handler);
}

/// <summary>CancelContract step (post-stage).</summary>
public interface ICancelContractPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCancelContract)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICancelContractPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICancelContractPostContext<TEntity>> handler);
}

/// <summary>Stage selection for CancelContract.</summary>
public interface ICancelContractStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ICancelContractPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ICancelContractPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ICancelContractPostStep<TEntity>> PostOperation();
}

/// <summary>CloneContract step (pre-stage).</summary>
public interface ICloneContractPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCloneContract)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICloneContractPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICloneContractPreContext<TEntity>> handler);
}

/// <summary>CloneContract step (post-stage).</summary>
public interface ICloneContractPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnCloneContract)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(ICloneContractPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<ICloneContractPostContext<TEntity>> handler);
}

/// <summary>Stage selection for CloneContract.</summary>
public interface ICloneContractStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<ICloneContractPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<ICloneContractPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<ICloneContractPostStep<TEntity>> PostOperation();
}

/// <summary>RetrievePrincipalAccess step (pre-stage).</summary>
public interface IRetrievePrincipalAccessPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRetrievePrincipalAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrievePrincipalAccessPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrievePrincipalAccessPreContext<TEntity>> handler);
}

/// <summary>RetrievePrincipalAccess step (post-stage).</summary>
public interface IRetrievePrincipalAccessPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRetrievePrincipalAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrievePrincipalAccessPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrievePrincipalAccessPostContext<TEntity>> handler);
}

/// <summary>Stage selection for RetrievePrincipalAccess.</summary>
public interface IRetrievePrincipalAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRetrievePrincipalAccessPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRetrievePrincipalAccessPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRetrievePrincipalAccessPostStep<TEntity>> PostOperation();
}

/// <summary>RetrieveSharedPrincipalsAndAccess step (pre-stage).</summary>
public interface IRetrieveSharedPrincipalsAndAccessPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRetrieveSharedPrincipalsAndAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrieveSharedPrincipalsAndAccessPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrieveSharedPrincipalsAndAccessPreContext<TEntity>> handler);
}

/// <summary>RetrieveSharedPrincipalsAndAccess step (post-stage).</summary>
public interface IRetrieveSharedPrincipalsAndAccessPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnRetrieveSharedPrincipalsAndAccess)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrieveSharedPrincipalsAndAccessPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrieveSharedPrincipalsAndAccessPostContext<TEntity>> handler);
}

/// <summary>Stage selection for RetrieveSharedPrincipalsAndAccess.</summary>
public interface IRetrieveSharedPrincipalsAndAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IRetrieveSharedPrincipalsAndAccessPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IRetrieveSharedPrincipalsAndAccessPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IRetrieveSharedPrincipalsAndAccessPostStep<TEntity>> PostOperation();
}

/// <summary>WinQuote step (pre-stage).</summary>
public interface IWinQuotePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnWinQuote)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IWinQuotePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IWinQuotePreContext<TEntity>> handler);
}

/// <summary>WinQuote step (post-stage).</summary>
public interface IWinQuotePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnWinQuote)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IWinQuotePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IWinQuotePostContext<TEntity>> handler);
}

/// <summary>Stage selection for WinQuote.</summary>
public interface IWinQuoteStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IWinQuotePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IWinQuotePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IWinQuotePostStep<TEntity>> PostOperation();
}

/// <summary>ReviseQuote step (pre-stage).</summary>
public interface IReviseQuotePreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnReviseQuote)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IReviseQuotePreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IReviseQuotePreContext<TEntity>> handler);
}

/// <summary>ReviseQuote step (post-stage).</summary>
public interface IReviseQuotePostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnReviseQuote)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IReviseQuotePostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IReviseQuotePostContext<TEntity>> handler);
}

/// <summary>Stage selection for ReviseQuote.</summary>
public interface IReviseQuoteStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IReviseQuotePreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IReviseQuotePreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IReviseQuotePostStep<TEntity>> PostOperation();
}

/// <summary>FulfillSalesOrder step (pre-stage).</summary>
public interface IFulfillSalesOrderPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnFulfillSalesOrder)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IFulfillSalesOrderPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IFulfillSalesOrderPreContext<TEntity>> handler);
}

/// <summary>FulfillSalesOrder step (post-stage).</summary>
public interface IFulfillSalesOrderPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnFulfillSalesOrder)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IFulfillSalesOrderPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IFulfillSalesOrderPostContext<TEntity>> handler);
}

/// <summary>Stage selection for FulfillSalesOrder.</summary>
public interface IFulfillSalesOrderStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IFulfillSalesOrderPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IFulfillSalesOrderPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IFulfillSalesOrderPostStep<TEntity>> PostOperation();
}

/// <summary>GetQuoteProductsFromOpportunity step (pre-stage).</summary>
public interface IGetQuoteProductsFromOpportunityPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGetQuoteProductsFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGetQuoteProductsFromOpportunityPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGetQuoteProductsFromOpportunityPreContext<TEntity>> handler);
}

/// <summary>GetQuoteProductsFromOpportunity step (post-stage).</summary>
public interface IGetQuoteProductsFromOpportunityPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGetQuoteProductsFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGetQuoteProductsFromOpportunityPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGetQuoteProductsFromOpportunityPostContext<TEntity>> handler);
}

/// <summary>Stage selection for GetQuoteProductsFromOpportunity.</summary>
public interface IGetQuoteProductsFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGetQuoteProductsFromOpportunityPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGetQuoteProductsFromOpportunityPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGetQuoteProductsFromOpportunityPostStep<TEntity>> PostOperation();
}

/// <summary>GetSalesOrderProductsFromOpportunity step (pre-stage).</summary>
public interface IGetSalesOrderProductsFromOpportunityPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGetSalesOrderProductsFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGetSalesOrderProductsFromOpportunityPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGetSalesOrderProductsFromOpportunityPreContext<TEntity>> handler);
}

/// <summary>GetSalesOrderProductsFromOpportunity step (post-stage).</summary>
public interface IGetSalesOrderProductsFromOpportunityPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGetSalesOrderProductsFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGetSalesOrderProductsFromOpportunityPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGetSalesOrderProductsFromOpportunityPostContext<TEntity>> handler);
}

/// <summary>Stage selection for GetSalesOrderProductsFromOpportunity.</summary>
public interface IGetSalesOrderProductsFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGetSalesOrderProductsFromOpportunityPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGetSalesOrderProductsFromOpportunityPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGetSalesOrderProductsFromOpportunityPostStep<TEntity>> PostOperation();
}

/// <summary>GetInvoiceProductsFromOpportunity step (pre-stage).</summary>
public interface IGetInvoiceProductsFromOpportunityPreStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGetInvoiceProductsFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGetInvoiceProductsFromOpportunityPreContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGetInvoiceProductsFromOpportunityPreContext<TEntity>> handler);
}

/// <summary>GetInvoiceProductsFromOpportunity step (post-stage).</summary>
public interface IGetInvoiceProductsFromOpportunityPostStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler, e.g. <c>.Handler(OnGetInvoiceProductsFromOpportunity)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IGetInvoiceProductsFromOpportunityPostContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGetInvoiceProductsFromOpportunityPostContext<TEntity>> handler);
}

/// <summary>Stage selection for GetInvoiceProductsFromOpportunity.</summary>
public interface IGetInvoiceProductsFromOpportunityStepStage<TEntity> where TEntity : Entity
{
    /// <summary>PreValidation.</summary>
    IModeSelector<IGetInvoiceProductsFromOpportunityPreStep<TEntity>> PreValidation();
    /// <summary>PreOperation.</summary>
    IModeSelector<IGetInvoiceProductsFromOpportunityPreStep<TEntity>> PreOperation();
    /// <summary>PostOperation.</summary>
    IModeSelector<IGetInvoiceProductsFromOpportunityPostStep<TEntity>> PostOperation();
}

