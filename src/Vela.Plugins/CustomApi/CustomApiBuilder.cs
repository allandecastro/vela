using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Vela.Constants;
using Vela.StepBuilder;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.CustomApi;

/// <summary>
/// Fluent builder for Custom API metadata.
/// </summary>
/// <typeparam name="TRequest">The request model type.</typeparam>
/// <typeparam name="TResponse">The response model type.</typeparam>
public sealed class CustomApiBuilder<TRequest, TResponse> : ICustomApiBuilder<TRequest, TResponse>
    where TRequest : class, new()
    where TResponse : class, new()
{
    private readonly CustomApiDefinition _definition = new()
    {
        RequestTypeName = typeof(TRequest).FullName ?? typeof(TRequest).Name,
        ResponseTypeName = typeof(TResponse).FullName ?? typeof(TResponse).Name
    };
    private readonly List<CustomApiParameterDefinition> _requestParameters = new();
    private readonly List<CustomApiParameterDefinition> _responseParameters = new();
    private bool _requestConfigured;
    private bool _responseConfigured;

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> UniqueName(string uniqueName)
    {
        _definition.UniqueName = RequireValue(uniqueName, nameof(uniqueName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> DisplayName(string displayName)
    {
        _definition.DisplayName = RequireValue(displayName, nameof(displayName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> Description(string description)
    {
        _definition.Description = description ?? string.Empty;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> AsFunction()
    {
        _definition.IsFunction = true;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> AsAction()
    {
        _definition.IsFunction = false;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> BoundTo<TEntity>() where TEntity : Entity
    {
        var attribute = typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>();
        return BoundTo(attribute?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant());
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> BoundTo(string entityLogicalName)
    {
        _definition.IsBound = true;
        _definition.BindingType = CustomApiBindingType.Entity;
        _definition.BoundEntityLogicalName = RequireValue(entityLogicalName, nameof(entityLogicalName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> BoundToCollection<TEntity>() where TEntity : Entity
    {
        var attribute = typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>();
        return BoundToCollection(attribute?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant());
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> BoundToCollection(string entityLogicalName)
    {
        _definition.IsBound = true;
        _definition.BindingType = CustomApiBindingType.EntityCollection;
        _definition.BoundEntityLogicalName = RequireValue(entityLogicalName, nameof(entityLogicalName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> ExecutePrivilege(string privilegeName)
    {
        _definition.ExecutePrivilegeName = RequireValue(privilegeName, nameof(privilegeName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> Private()
    {
        _definition.IsPrivate = true;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> Public()
    {
        _definition.IsPrivate = false;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType stepType)
    {
        _definition.AllowedCustomProcessingStepType = stepType;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> WorkflowSdkStep(bool enabled)
    {
        _definition.WorkflowSdkStepEnabled = enabled;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> Request(Action<ICustomApiParameterBuilder<TRequest>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _requestConfigured = true;
        _requestParameters.Clear();
        configure(new CustomApiParameterBuilder<TRequest>(_requestParameters));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> Response(Action<ICustomApiParameterBuilder<TResponse>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _responseConfigured = true;
        _responseParameters.Clear();
        configure(new CustomApiParameterBuilder<TResponse>(_responseParameters));
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

        _definition.InputParameters = _requestConfigured
            ? _requestParameters.ToArray()
            : BuildParameters(typeof(TRequest), isOutput: false);

        _definition.OutputParameters = _responseConfigured
            ? _responseParameters.ToArray()
            : BuildParameters(typeof(TResponse), isOutput: true);

        _definition.PluginStep = new StepDefinition
        {
            PluginTypeName = _definition.PluginTypeName,
            Message = _definition.UniqueName,
            Stage = PluginStages.MainOperation,
            Mode = ExecutionMode.Synchronous
        };

        return _definition;
    }

    private static CustomApiParameterDefinition[] BuildParameters(Type parameterType, bool isOutput)
    {
        List<CustomApiParameterDefinition> parameters = new List<CustomApiParameterDefinition>();
        foreach (PropertyInfo property in parameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || (!isOutput && !property.CanWrite))
            {
                continue;
            }

            parameters.Add(new CustomApiParameterDefinition
            {
                Name = property.Name,
                ParameterTypeName = Nullable.GetUnderlyingType(property.PropertyType)?.FullName ?? property.PropertyType.FullName ?? property.PropertyType.Name,
                IsOptional = !isOutput && property.GetCustomAttribute<RequiredAttribute>() == null,
                IsCollection = IsCollectionType(property.PropertyType),
                Description = property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty
            });
        }

        return parameters.ToArray();
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

    private sealed class CustomApiParameterBuilder<TModel>(ICollection<CustomApiParameterDefinition> parameters) : ICustomApiParameterBuilder<TModel>
        where TModel : class
    {
        private readonly ICollection<CustomApiParameterDefinition> _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));

        public ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property)
        {
            return Add(property, optional: false, description: null);
        }

        public ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property, bool optional, string? description = null)
        {
            property = property ?? throw new ArgumentNullException(nameof(property));

            MemberExpression? member = GetMemberExpression(property);
            if (member == null || member.Member is not PropertyInfo propertyInfo)
            {
                throw new ArgumentException("Expression must target a property.", nameof(property));
            }

            Type propertyType = propertyInfo.PropertyType;
            _parameters.Add(new CustomApiParameterDefinition
            {
                Name = propertyInfo.Name,
                ParameterTypeName = Nullable.GetUnderlyingType(propertyType)?.FullName ?? propertyType.FullName ?? propertyType.Name,
                IsOptional = optional || propertyInfo.GetCustomAttribute<RequiredAttribute>() == null,
                IsCollection = IsCollectionType(propertyType),
                Description = description ?? propertyInfo.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty
            });

            return this;
        }

        private static MemberExpression? GetMemberExpression<TProperty>(Expression<Func<TModel, TProperty>> property)
        {
            Expression body = property.Body;
            if (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            {
                body = unary.Operand;
            }

            return body as MemberExpression;
        }
    }
}
