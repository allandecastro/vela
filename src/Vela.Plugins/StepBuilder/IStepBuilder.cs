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
    ICreateStepStage<TEntity> OnCreateMultiple();

    /// <summary>
    /// Configure step to trigger on the UpdateMultiple (bulk update) message. The step fires once with the
    /// whole <c>Targets</c> collection (read it via <c>ctx.BulkTargets</c>). Filtering attributes are required.
    /// </summary>
    IUpdateStepStage<TEntity> OnUpdateMultiple();

    /// <summary>Configure step to trigger on Delete message.</summary>
    IDeleteStepStage<TEntity> OnDelete();

    /// <summary>Configure step to trigger on Retrieve message.</summary>
    IReferenceStepStage<TEntity> OnRetrieve();

    /// <summary>Configure step to trigger on RetrieveMultiple message.</summary>
    IReferenceStepStage<TEntity> OnRetrieveMultiple();

    /// <summary>Configure step to trigger on Associate message.</summary>
    IReferenceStepStage<TEntity> OnAssociate();

    /// <summary>Configure step to trigger on Disassociate message.</summary>
    IReferenceStepStage<TEntity> OnDisassociate();

    /// <summary>Configure step to trigger on the Merge message (handler receives an <c>IMergeContext</c>).</summary>
    IMergeStepStage<TEntity> OnMerge();

    /// <summary>Configure step to trigger on a custom message. Image validity is enforced at runtime.</summary>
    /// <param name="messageName">The SDK message name.</param>
    IPermissiveStepStage<TEntity> OnMessage(string messageName);

    /// <summary>Configure step to trigger on a typed custom message. Image validity is enforced at runtime.</summary>
    /// <typeparam name="TMessage">The message marker type.</typeparam>
    IPermissiveStepStage<TEntity> OnMessage<TMessage>() where TMessage : class;
}
