using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.CustomApi;

/// <summary>
/// The shared core of the Custom API builders: the metadata methods, parameter creation and the validation that
/// <c>Build()</c> runs. Use <see cref="CustomApiBuilder{TRequest, TResponse}"/> or <see cref="CustomApiLightBuilder"/>.
/// </summary>
/// <typeparam name="TSelf">The builder interface the metadata methods return.</typeparam>
public abstract class CustomApiBuilderBase<TSelf> : ICustomApiMetadata<TSelf>
    where TSelf : class
{
    private static readonly string[] WorkflowUnsupportedTypes = ["Entity", "EntityCollection", "StringArray"];

    private protected CustomApiBuilderBase()
    {
    }

    private protected CustomApiDefinition Definition { get; } = new();

    private protected List<CustomApiParameterDefinition> RequestParameters { get; } = new();

    private protected List<CustomApiParameterDefinition> ResponseParameters { get; } = new();

    private TSelf Self => (TSelf)(object)this;

    /// <inheritdoc />
    public TSelf UniqueName(string uniqueName)
    {
        Definition.UniqueName = RequireValue(uniqueName, nameof(uniqueName));
        return Self;
    }

    /// <inheritdoc />
    public TSelf DisplayName(string displayName)
    {
        Definition.DisplayName = RequireValue(displayName, nameof(displayName));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Description(string description)
    {
        Definition.Description = description ?? string.Empty;
        return Self;
    }

    /// <inheritdoc />
    public TSelf AsFunction()
    {
        Definition.IsFunction = true;
        return Self;
    }

    /// <inheritdoc />
    public TSelf AsAction()
    {
        Definition.IsFunction = false;
        return Self;
    }

    /// <inheritdoc />
    public TSelf BoundTo<TEntity>() where TEntity : Entity => BoundTo(EntityName<TEntity>());

    /// <inheritdoc />
    public TSelf BoundTo(string entityLogicalName) => Bind(CustomApiBindingType.Entity, entityLogicalName);

    /// <inheritdoc />
    public TSelf BoundToCollection<TEntity>() where TEntity : Entity => BoundToCollection(EntityName<TEntity>());

    /// <inheritdoc />
    public TSelf BoundToCollection(string entityLogicalName) => Bind(CustomApiBindingType.EntityCollection, entityLogicalName);

    /// <inheritdoc />
    public TSelf ExecutePrivilege(string privilegeName)
    {
        Definition.ExecutePrivilegeName = RequireValue(privilegeName, nameof(privilegeName));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Private()
    {
        Definition.IsPrivate = true;
        return Self;
    }

    /// <inheritdoc />
    public TSelf Public()
    {
        Definition.IsPrivate = false;
        return Self;
    }

    /// <inheritdoc />
    public TSelf AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType stepType)
    {
        Definition.AllowedCustomProcessingStepType = stepType;
        return Self;
    }

    /// <inheritdoc />
    public TSelf WorkflowSdkStep(bool enabled)
    {
        Definition.WorkflowSdkStepEnabled = enabled;
        return Self;
    }

    private protected static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentNullException(parameterName);
        }

        return value;
    }

    /// <summary>
    /// Describes one parameter. A request parameter is required unless <paramref name="optional"/>; a response
    /// property has no optionality. The table defaults to the early-bound entity's logical name.
    /// </summary>
    private protected static CustomApiParameterDefinition CreateParameter(
        string name, Type clrType, bool isOutput, bool optional, string? description, string? logicalEntityName)
    {
        RequireValue(name, nameof(name));
        if (!name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException(
                $"Custom API parameter name '{name}' is not valid: a parameter's unique name can contain only letters, digits and underscores.",
                nameof(name));
        }

        (string typeName, bool isCollection) = CustomApiParameterTypes.Describe(clrType, name);
        string? table = string.IsNullOrWhiteSpace(logicalEntityName) ? CustomApiParameterTypes.EarlyBoundLogicalName(clrType) : logicalEntityName;
        if (table != null)
        {
            // Dataverse: a response property's table applies to Entity and EntityReference only (not EntityCollection);
            // a request parameter's table to the entity types.
            string label = CustomApiParameterTypes.Label(typeName, isCollection);
            string[] allowed = isOutput ? ["Entity", "EntityReference"] : ["Entity", "EntityReference", "EntityCollection"];
            if (!allowed.Contains(label))
            {
                throw new ArgumentException(
                    $"Custom API {(isOutput ? "response property" : "request parameter")} '{name}' declares the table '{table}', but it is a {label}. " +
                    $"A table applies only to a {string.Join(" or ", allowed)} {(isOutput ? "response property" : "request parameter")}.",
                    nameof(logicalEntityName));
            }
        }

        return new CustomApiParameterDefinition
        {
            Name = name,
            ParameterTypeName = typeName,
            IsOptional = !isOutput && optional,
            IsCollection = isCollection,
            LogicalEntityName = table,
            Description = description ?? string.Empty
        };
    }

    /// <summary>Adds <paramref name="parameter"/>, refusing a second parameter of the same name (case-insensitive).</summary>
    private protected static void AddParameter(ICollection<CustomApiParameterDefinition> parameters, CustomApiParameterDefinition parameter)
    {
        if (parameters.Any(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Custom API parameter '{parameter.Name}' was added more than once. Each request/response parameter must be added exactly once (across all .Request()/.Response() calls).");
        }

        parameters.Add(parameter);
    }

    /// <summary>
    /// Completes and validates the definition against the Dataverse Custom API rules, then returns it.
    /// </summary>
    private protected CustomApiDefinition Complete(CustomApiParameterDefinition[] inputs, CustomApiParameterDefinition[] outputs)
    {
        CustomApiDefinition definition = Definition;
        if (string.IsNullOrWhiteSpace(definition.UniqueName))
        {
            throw new InvalidOperationException("Custom API unique name is required.");
        }

        if (string.IsNullOrWhiteSpace(definition.DisplayName))
        {
            definition.DisplayName = definition.UniqueName;
        }

        definition.InputParameters = inputs;
        definition.OutputParameters = outputs;
        Validate(definition);
        definition.PluginStep = definition.CreatePluginStep();
        return definition;
    }

    // Each rule is a documented Dataverse Custom API constraint (custom-api and custom-api-tables on Microsoft Learn),
    // checked here so the plugin fails when the step is built instead of at deployment with a platform error.
    private static void Validate(CustomApiDefinition definition)
    {
        string api = definition.UniqueName;

        // "Functions must return some data. You must include at least one response property."
        if (definition.IsFunction && definition.OutputParameters.Length == 0)
        {
            throw new InvalidOperationException(
                $"Custom API '{api}' is a function but declares no response property. Dataverse requires a function to return at least one " +
                "response property (otherwise it is not callable): add one, or make it an action (AsAction()).");
        }

        if (!definition.WorkflowSdkStepEnabled)
        {
            return;
        }

        // "Enabled for Workflow": Is Function must be false; Entity, EntityCollection and StringArray can't be used;
        // EntityReference only when the custom API is bound to an entity.
        if (definition.IsFunction)
        {
            throw new InvalidOperationException(
                $"Custom API '{api}': WorkflowSdkStep(true) requires an action, but it is a function. Make it an action (AsAction()) or disable workflow support.");
        }

        foreach (CustomApiParameterDefinition parameter in definition.InputParameters.Concat(definition.OutputParameters))
        {
            string label = CustomApiParameterTypes.Label(parameter.ParameterTypeName, parameter.IsCollection);
            if (WorkflowUnsupportedTypes.Contains(label))
            {
                throw new InvalidOperationException(
                    $"Custom API '{api}': parameter '{parameter.Name}' is a {label}, which a workflow-enabled Custom API cannot use " +
                    "(Entity, EntityCollection and StringArray are not available in workflows). Change its type or disable workflow support.");
            }

            if (label == "EntityReference" && definition.BindingType != CustomApiBindingType.Entity)
            {
                throw new InvalidOperationException(
                    $"Custom API '{api}': parameter '{parameter.Name}' is an EntityReference, which a workflow-enabled Custom API can use only " +
                    "when it is bound to a table (BoundTo<TEntity>()). Bind the API, change the type or disable workflow support.");
            }
        }
    }

    private TSelf Bind(CustomApiBindingType bindingType, string entityLogicalName)
    {
        Definition.IsBound = true;
        Definition.BindingType = bindingType;
        Definition.BoundEntityLogicalName = RequireValue(entityLogicalName, nameof(entityLogicalName));
        return Self;
    }

    private static string EntityName<TEntity>() where TEntity : Entity
        => typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>()?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant();
}
