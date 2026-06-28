namespace Vela.CustomApi;

/// <summary>
/// Controls which plugin step types are allowed for a Custom API.
/// </summary>
public enum CustomApiAllowedCustomProcessingStepType
{
    /// <summary>
    /// No additional custom processing steps are allowed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Only asynchronous custom processing steps are allowed.
    /// </summary>
    AsyncOnly = 1,

    /// <summary>
    /// Both synchronous and asynchronous custom processing steps are allowed.
    /// </summary>
    SyncAndAsync = 2
}

/// <summary>
/// Defines the Custom API binding type.
/// </summary>
public enum CustomApiBindingType
{
    /// <summary>
    /// API is not bound to an entity.
    /// </summary>
    Global = 0,

    /// <summary>
    /// API is bound to a single entity.
    /// </summary>
    Entity = 1,

    /// <summary>
    /// API is bound to an entity collection.
    /// </summary>
    EntityCollection = 2
}
