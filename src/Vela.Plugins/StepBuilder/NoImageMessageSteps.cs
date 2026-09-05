using System;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

// Per-message typed steps for the no-image messages (Retrieve / RetrieveMultiple / Associate / Disassociate /
// GrantAccess / ModifyAccess / RevokeAccess). Each extends the shared INoImageStep terminal (settings/build
// inherited, not duplicated) and adds a message-typed .Handler; each stage selector returns it. Adding a
// no-image message is exactly this pattern.

/// <summary>Retrieve step: <c>.Handler(OnRetrieved)</c> where the handler takes an <c>IRetrieveContext</c>.</summary>
public interface IRetrieveStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler (method group), e.g. <c>.Handler(OnRetrieved)</c>.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrieveContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrieveContext<TEntity>> handler);
}

/// <summary>Stage selection for Retrieve (no images).</summary>
public interface IRetrieveStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage.</summary>
    IModeSelector<IRetrieveStep<TEntity>> PreValidation();
    /// <summary>Register in the PreOperation stage.</summary>
    IModeSelector<IRetrieveStep<TEntity>> PreOperation();
    /// <summary>Register in the PostOperation stage.</summary>
    IModeSelector<IRetrieveStep<TEntity>> PostOperation();
}

/// <summary>RetrieveMultiple step.</summary>
public interface IRetrieveMultipleStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary>
    /// <param name="handler">Instance method with signature <c>(IRetrieveMultipleContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRetrieveMultipleContext<TEntity>> handler);
}

/// <summary>Stage selection for RetrieveMultiple (no images).</summary>
public interface IRetrieveMultipleStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage.</summary>
    IModeSelector<IRetrieveMultipleStep<TEntity>> PreValidation();
    /// <summary>Register in the PreOperation stage.</summary>
    IModeSelector<IRetrieveMultipleStep<TEntity>> PreOperation();
    /// <summary>Register in the PostOperation stage.</summary>
    IModeSelector<IRetrieveMultipleStep<TEntity>> PostOperation();
}

/// <summary>Associate step.</summary>
public interface IAssociateStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary>
    /// <param name="handler">Instance method with signature <c>(IAssociateContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IAssociateContext<TEntity>> handler);
}

/// <summary>Stage selection for Associate (no images).</summary>
public interface IAssociateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage.</summary>
    IModeSelector<IAssociateStep<TEntity>> PreValidation();
    /// <summary>Register in the PreOperation stage.</summary>
    IModeSelector<IAssociateStep<TEntity>> PreOperation();
    /// <summary>Register in the PostOperation stage.</summary>
    IModeSelector<IAssociateStep<TEntity>> PostOperation();
}

/// <summary>Disassociate step.</summary>
public interface IDisassociateStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary>
    /// <param name="handler">Instance method with signature <c>(IDisassociateContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IDisassociateContext<TEntity>> handler);
}

/// <summary>Stage selection for Disassociate (no images).</summary>
public interface IDisassociateStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage.</summary>
    IModeSelector<IDisassociateStep<TEntity>> PreValidation();
    /// <summary>Register in the PreOperation stage.</summary>
    IModeSelector<IDisassociateStep<TEntity>> PreOperation();
    /// <summary>Register in the PostOperation stage.</summary>
    IModeSelector<IDisassociateStep<TEntity>> PostOperation();
}

/// <summary>GrantAccess step.</summary>
public interface IGrantAccessStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary>
    /// <param name="handler">Instance method with signature <c>(IGrantAccessContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IGrantAccessContext<TEntity>> handler);
}

/// <summary>Stage selection for GrantAccess (no images).</summary>
public interface IGrantAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage.</summary>
    IModeSelector<IGrantAccessStep<TEntity>> PreValidation();
    /// <summary>Register in the PreOperation stage.</summary>
    IModeSelector<IGrantAccessStep<TEntity>> PreOperation();
    /// <summary>Register in the PostOperation stage.</summary>
    IModeSelector<IGrantAccessStep<TEntity>> PostOperation();
}

/// <summary>ModifyAccess step.</summary>
public interface IModifyAccessStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary>
    /// <param name="handler">Instance method with signature <c>(IModifyAccessContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IModifyAccessContext<TEntity>> handler);
}

/// <summary>Stage selection for ModifyAccess (no images).</summary>
public interface IModifyAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage.</summary>
    IModeSelector<IModifyAccessStep<TEntity>> PreValidation();
    /// <summary>Register in the PreOperation stage.</summary>
    IModeSelector<IModifyAccessStep<TEntity>> PreOperation();
    /// <summary>Register in the PostOperation stage.</summary>
    IModeSelector<IModifyAccessStep<TEntity>> PostOperation();
}

/// <summary>RevokeAccess step.</summary>
public interface IRevokeAccessStep<TEntity> : INoImageStep<TEntity> where TEntity : Entity
{
    /// <summary>Bind a typed handler.</summary>
    /// <param name="handler">Instance method with signature <c>(IRevokeAccessContext&lt;TEntity&gt;)</c>.</param>
    INoImageStep<TEntity> Handler(Action<IRevokeAccessContext<TEntity>> handler);
}

/// <summary>Stage selection for RevokeAccess (no images).</summary>
public interface IRevokeAccessStepStage<TEntity> where TEntity : Entity
{
    /// <summary>Register in the PreValidation stage.</summary>
    IModeSelector<IRevokeAccessStep<TEntity>> PreValidation();
    /// <summary>Register in the PreOperation stage.</summary>
    IModeSelector<IRevokeAccessStep<TEntity>> PreOperation();
    /// <summary>Register in the PostOperation stage.</summary>
    IModeSelector<IRevokeAccessStep<TEntity>> PostOperation();
}
