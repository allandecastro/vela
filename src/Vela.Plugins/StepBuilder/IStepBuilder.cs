using Vela.Constants;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Defines the step configuration starting point - message selection.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IStepBuilder<TEntity> where TEntity : Entity
{
    /// <summary>Configure step to trigger on Create message. Filtering attributes are required.</summary>
    IFilteringStepMessageBuilder<TEntity> OnCreate();

    /// <summary>Configure step to trigger on Update message. Filtering attributes are required.</summary>
    IFilteringStepMessageBuilder<TEntity> OnUpdate();

    /// <summary>
    /// Configure step to trigger on the CreateMultiple (bulk create) message. The step fires once with the
    /// whole <c>Targets</c> collection (read it via <c>ctx.BulkTargets</c>). Filtering attributes are required.
    /// </summary>
    IFilteringStepMessageBuilder<TEntity> OnCreateMultiple();

    /// <summary>
    /// Configure step to trigger on the UpdateMultiple (bulk update) message. The step fires once with the
    /// whole <c>Targets</c> collection (read it via <c>ctx.BulkTargets</c>). Filtering attributes are required.
    /// </summary>
    IFilteringStepMessageBuilder<TEntity> OnUpdateMultiple();

    /// <summary>Configure step to trigger on Delete message.</summary>
    IStepMessageBuilder<TEntity> OnDelete();

    /// <summary>Configure step to trigger on Retrieve message.</summary>
    IStepMessageBuilder<TEntity> OnRetrieve();

    /// <summary>Configure step to trigger on RetrieveMultiple message.</summary>
    IStepMessageBuilder<TEntity> OnRetrieveMultiple();

    /// <summary>Configure step to trigger on Associate message.</summary>
    IStepMessageBuilder<TEntity> OnAssociate();

    /// <summary>Configure step to trigger on Disassociate message.</summary>
    IStepMessageBuilder<TEntity> OnDisassociate();

    /// <summary>Configure step to trigger on SetState message.</summary>
    IStepMessageBuilder<TEntity> OnSetState();

    /// <summary>Configure step to trigger on SetStateDynamicEntity message.</summary>
    IStepMessageBuilder<TEntity> OnSetStateDynamicEntity();

    /// <summary>Configure step to trigger on a custom message.</summary>
    /// <param name="messageName">The SDK message name.</param>
    IStepMessageBuilder<TEntity> OnMessage(string messageName);

    /// <summary>Configure step to trigger on a typed custom message.</summary>
    /// <typeparam name="TMessage">The message marker type.</typeparam>
    IStepMessageBuilder<TEntity> OnMessage<TMessage>() where TMessage : class;
}
