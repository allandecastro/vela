using System;
using System.Collections.Generic;
using System.Reflection;
using Vela.Constants;
using Vela.Context;
using Vela.Steps;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.CustomApi;

/// <summary>
/// Lightweight builder for Custom API metadata without dedicated CLR request/response models.
/// </summary>
public sealed class CustomApiLightBuilder : ICustomApiBuilder
{
    private readonly CustomApiDefinition _definition = new();
    private readonly List<CustomApiParameterDefinition> _requestParameters = new();
    private readonly List<CustomApiParameterDefinition> _responseParameters = new();

    /// <inheritdoc />
    public ICustomApiBuilder UniqueName(string uniqueName)
    {
        _definition.UniqueName = RequireValue(uniqueName, nameof(uniqueName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder DisplayName(string displayName)
    {
        _definition.DisplayName = RequireValue(displayName, nameof(displayName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder Description(string description)
    {
        _definition.Description = description ?? string.Empty;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder AsFunction()
    {
        _definition.IsFunction = true;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder AsAction()
    {
        _definition.IsFunction = false;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder BoundTo<TEntity>() where TEntity : Entity
    {
        EntityLogicalNameAttribute? attribute = typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>();
        return BoundTo(attribute?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant());
    }

    /// <inheritdoc />
    public ICustomApiBuilder BoundTo(string entityLogicalName)
    {
        _definition.IsBound = true;
        _definition.BindingType = CustomApiBindingType.Entity;
        _definition.BoundEntityLogicalName = RequireValue(entityLogicalName, nameof(entityLogicalName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder BoundToCollection<TEntity>() where TEntity : Entity
    {
        EntityLogicalNameAttribute? attribute = typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>();
        return BoundToCollection(attribute?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant());
    }

    /// <inheritdoc />
    public ICustomApiBuilder BoundToCollection(string entityLogicalName)
    {
        _definition.IsBound = true;
        _definition.BindingType = CustomApiBindingType.EntityCollection;
        _definition.BoundEntityLogicalName = RequireValue(entityLogicalName, nameof(entityLogicalName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder ExecutePrivilege(string privilegeName)
    {
        _definition.ExecutePrivilegeName = RequireValue(privilegeName, nameof(privilegeName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder Private()
    {
        _definition.IsPrivate = true;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder Public()
    {
        _definition.IsPrivate = false;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType stepType)
    {
        _definition.AllowedCustomProcessingStepType = stepType;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder WorkflowSdkStep(bool enabled)
    {
        _definition.WorkflowSdkStepEnabled = enabled;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder Handler(string methodName)
    {
        _definition.HandlerMethodName = RequireValue(methodName, nameof(methodName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder Handler(Action<ILocalPluginContext> handler)
    {
        handler = handler ?? throw new ArgumentNullException(nameof(handler));
        if (handler.Method.IsStatic)
        {
            throw new ArgumentException("Handler delegate must reference an instance method.", nameof(handler));
        }

        return Handler(handler.Method.Name);
    }

    /// <inheritdoc />
    public ICustomApiBuilder RequestParameter<TParameter>(string name, bool optional = false, string? description = null)
    {
        return RequestParameter(name, typeof(TParameter), optional, description);
    }

    /// <inheritdoc />
    public ICustomApiBuilder RequestParameter(string name, Type parameterType, bool optional = false, string? description = null)
    {
        _requestParameters.Add(CreateParameterDefinition(name, parameterType, optional, description));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder ResponseParameter<TParameter>(string name, string? description = null)
    {
        return ResponseParameter(name, typeof(TParameter), description);
    }

    /// <inheritdoc />
    public ICustomApiBuilder ResponseParameter(string name, Type parameterType, string? description = null)
    {
        _responseParameters.Add(CreateParameterDefinition(name, parameterType, optional: false, description));
        return this;
    }

    /// <inheritdoc />
    public CustomApiDefinition Build()
    {
        if (string.IsNullOrWhiteSpace(_definition.UniqueName))
        {
            throw new InvalidOperationException("Custom API unique name is required.");
        }

        if (string.IsNullOrWhiteSpace(_definition.DisplayName))
        {
            _definition.DisplayName = _definition.UniqueName;
        }

        _definition.RequestTypeName = string.Empty;
        _definition.ResponseTypeName = string.Empty;
        _definition.InputParameters = _requestParameters.ToArray();
        _definition.OutputParameters = _responseParameters.ToArray();
        _definition.PluginStep = new StepDefinition
        {
            PluginTypeName = _definition.PluginTypeName,
            Message = _definition.UniqueName,
            Stage = PluginStages.MainOperation,
            Mode = ExecutionMode.Synchronous,
            HandlerMethodName = _definition.HandlerMethodName
        };

        return _definition;
    }

    private static CustomApiParameterDefinition CreateParameterDefinition(string name, Type parameterType, bool optional, string? description)
    {
        parameterType = parameterType ?? throw new ArgumentNullException(nameof(parameterType));

        return new CustomApiParameterDefinition
        {
            Name = RequireValue(name, nameof(name)),
            ParameterTypeName = Nullable.GetUnderlyingType(parameterType)?.FullName ?? parameterType.FullName ?? parameterType.Name,
            IsOptional = optional,
            IsCollection = IsCollectionType(parameterType),
            Description = description ?? string.Empty
        };
    }

    private static bool IsCollectionType(Type type)
    {
        return type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentNullException(parameterName);
        }

        return value;
    }
}
