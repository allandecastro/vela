using System;
using Vela.Context;
using Vela.Steps;

namespace Vela.CustomApi;

/// <summary>
/// Represents the metadata required to create or update a Dataverse Custom API.
/// </summary>
public sealed class CustomApiDefinition
{
    /// <summary>
    /// Gets or sets the plugin type full name.
    /// </summary>
    public string PluginTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Custom API unique name.
    /// </summary>
    public string UniqueName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the API is a function.
    /// </summary>
    public bool IsFunction { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the API is bound.
    /// </summary>
    public bool IsBound { get; set; }

    /// <summary>
    /// Gets or sets the bound entity logical name.
    /// </summary>
    public string? BoundEntityLogicalName { get; set; }

    /// <summary>
    /// Gets or sets the privilege required to execute the API.
    /// </summary>
    public string? ExecutePrivilegeName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the API is private.
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Gets or sets the allowed custom processing step type.
    /// </summary>
    public CustomApiAllowedCustomProcessingStepType AllowedCustomProcessingStepType { get; set; } = CustomApiAllowedCustomProcessingStepType.None;

    /// <summary>
    /// Gets or sets the binding type.
    /// </summary>
    public CustomApiBindingType BindingType { get; set; } = CustomApiBindingType.Global;

    /// <summary>
    /// Gets or sets a value indicating whether workflow SDK steps are enabled.
    /// </summary>
    public bool WorkflowSdkStepEnabled { get; set; }

    /// <summary>
    /// Gets or sets the request type name.
    /// </summary>
    public string RequestTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the response type name.
    /// </summary>
    public string ResponseTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the input parameters.
    /// </summary>
    public CustomApiParameterDefinition[] InputParameters { get; set; } = [];

    /// <summary>
    /// Gets or sets the output parameters.
    /// </summary>
    public CustomApiParameterDefinition[] OutputParameters { get; set; } = [];

    /// <summary>
    /// Gets or sets the plugin step used to execute the Custom API.
    /// </summary>
    public StepDefinition PluginStep { get; set; } = new();

    /// <summary>
    /// Gets or sets the handler method used to execute the Custom API.
    /// </summary>
    public string HandlerMethodName { get; set; } = string.Empty;

    // The handler delegate registered with Handler(Action<ILocalPluginContext>), invoked as is. A lambda compiles to a
    // method on a compiler-generated class (or a display class when it captures locals), so its name cannot be
    // resolved on the plug-in type; HandlerMethodName stays the delegate's method name for telemetry and traces.
    // Null when the handler was named with Handler(string). Set once while the definition is built under the
    // plug-in's initialization lock, then only read.
    internal Action<ILocalPluginContext>? RuntimeHandler { get; set; }

    // The main-operation step that runs the Custom API, derived from the definition's plugin type, unique name and
    // handler. The one place it is built (builders and both Custom API bases).
    internal StepDefinition CreatePluginStep() => new StepDefinition
    {
        PluginTypeName = PluginTypeName,
        Message = UniqueName,
        Stage = Constants.PluginStages.MainOperation,
        Mode = ExecutionMode.Synchronous,
        HandlerMethodName = HandlerMethodName
    };
}
