using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vela.Constants;
using Vela.Steps;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.CustomApi;

/// <summary>
/// Fluent builder for Custom API metadata.
/// </summary>
/// <typeparam name="TRequest">The request model type.</typeparam>
/// <typeparam name="TResponse">The response model type.</typeparam>
public sealed class CustomApiBuilder<TRequest, TResponse> : ICustomApiBuilder<TRequest, TResponse>
    where TRequest : OrganizationRequest, new()
    where TResponse : OrganizationResponse, new()
{
    // Members inherited from OrganizationRequest/OrganizationResponse are infrastructure, never Custom API
    // parameters. DeclaredOnly already excludes them; this set is a defensive backstop should a generated
    // type re-declare one of them.
    private static readonly HashSet<string> BaseMemberNames = new(StringComparer.Ordinal)
    {
        nameof(OrganizationRequest.RequestId),
        nameof(OrganizationRequest.RequestName),
        nameof(OrganizationRequest.Parameters),
        nameof(OrganizationResponse.ResponseName),
        nameof(OrganizationResponse.Results),
        "ExtensionData",
        "Item",
    };

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
        // Accumulate across calls: a second .Request(...) must add to the parameters, not replace them.
        // Clearing here silently dropped everything a prior call had added (only the last call survived).
        configure(new CustomApiParameterBuilder<TRequest>(_requestParameters));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> Response(Action<ICustomApiParameterBuilder<TResponse>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _responseConfigured = true;
        // Accumulate across calls (see Request): a second .Response(...) adds to the properties, not replaces them.
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

        // DeclaredOnly reflects only the request/response's own typed properties, never the inherited
        // OrganizationRequest/OrganizationResponse members (RequestName, Parameters, Results, the indexer, ...).
        foreach (PropertyInfo property in parameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.GetIndexParameters().Length > 0 || BaseMemberNames.Contains(property.Name))
            {
                continue;
            }

            if (!property.CanRead || (!isOutput && !property.CanWrite))
            {
                continue;
            }

            (string typeName, bool isCollection) = CustomApiParameterTypes.Describe(property.PropertyType, property.Name);
            parameters.Add(new CustomApiParameterDefinition
            {
                Name = property.Name,
                ParameterTypeName = typeName,
                IsOptional = !isOutput && property.GetCustomAttribute<RequiredAttribute>() == null,
                IsCollection = isCollection,
                Description = property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty
            });
        }

        return parameters.ToArray();
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

            if (_parameters.Any(p => string.Equals(p.Name, propertyInfo.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Custom API parameter '{propertyInfo.Name}' was added more than once. Each request/response parameter must be added exactly once (across all .Request()/.Response() calls).");
            }

            (string typeName, bool isCollection) = CustomApiParameterTypes.Describe(propertyInfo.PropertyType, propertyInfo.Name);
            _parameters.Add(new CustomApiParameterDefinition
            {
                Name = propertyInfo.Name,
                ParameterTypeName = typeName,
                IsOptional = optional || propertyInfo.GetCustomAttribute<RequiredAttribute>() == null,
                IsCollection = isCollection,
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
