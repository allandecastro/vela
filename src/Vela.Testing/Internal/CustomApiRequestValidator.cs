using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.CustomApi;

namespace Vela.Testing
{
    /// <summary>
    /// The request validation the platform applies to a Custom API call before anything runs (verified live, raised
    /// before any step, PreValidation included): a missing required input, an input of the wrong type, an undeclared
    /// input and, for a bound API, a missing or wrong-table <c>Target</c>. Shared by <see cref="PluginPipeline"/> and
    /// <see cref="PluginContext"/>, so both reject the same requests the same way.
    /// </summary>
    internal static class CustomApiRequestValidator
    {
        /// <summary>0x80040315: an input of the wrong type, or an undeclared one.</summary>
        public const int InvalidArgument = unchecked((int)0x80040315);

        /// <summary>0x80040203: a required input is missing.</summary>
        public const int MissingRequiredField = unchecked((int)0x80040203);

        // Request parameters every message accepts besides its own (the optional parameters of the SDK).
        private static readonly HashSet<string> OptionalRequestParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tag", "BypassCustomPluginExecution", "BypassBusinessLogicExecution", "BypassBusinessLogicExecutionStepIds",
            "SuppressDuplicateDetection", "SolutionUniqueName", "MaintainLegacyAppServerBehavior", "SuppressCallbackRegistrationExpanderJob",
            "partitionId",
        };

        private static readonly Dictionary<string, Type> ParameterTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [typeof(bool).FullName!] = typeof(bool),
            [typeof(DateTime).FullName!] = typeof(DateTime),
            [typeof(decimal).FullName!] = typeof(decimal),
            [typeof(double).FullName!] = typeof(double),
            [typeof(int).FullName!] = typeof(int),
            [typeof(Guid).FullName!] = typeof(Guid),
            [typeof(string).FullName!] = typeof(string),
            [typeof(Money).FullName!] = typeof(Money),
            [typeof(OptionSetValue).FullName!] = typeof(OptionSetValue),
            [typeof(EntityReference).FullName!] = typeof(EntityReference),
            [typeof(EntityCollection).FullName!] = typeof(EntityCollection),
            [typeof(Entity).FullName!] = typeof(Entity),
        };

        /// <summary>
        /// Rejects what the platform rejects before the Custom API runs. Returns the primary entity: the bound table
        /// and the Target id for a bound API, empty for a global one.
        /// </summary>
        /// <param name="api">The Custom API's message name, as the faults name it.</param>
        /// <param name="parameters">The request's input parameters.</param>
        /// <param name="definition">The Custom API's definition.</param>
        public static (string EntityName, Guid EntityId) Validate(string api, ParameterCollection parameters, CustomApiDefinition definition)
        {
            bool bound = definition.BindingType == CustomApiBindingType.Entity && !string.IsNullOrEmpty(definition.BoundEntityLogicalName);

            foreach (KeyValuePair<string, object> supplied in parameters)
            {
                bool declared = definition.InputParameters.Any(p => string.Equals(p.Name, supplied.Key, StringComparison.Ordinal))
                    || (bound && string.Equals(supplied.Key, "Target", StringComparison.Ordinal))
                    || OptionalRequestParameters.Contains(supplied.Key);
                if (!declared)
                {
                    throw PlatformFault.Create(InvalidArgument, $"Unrecognized request parameter: {supplied.Key}");
                }
            }

            foreach (CustomApiParameterDefinition parameter in definition.InputParameters)
            {
                if (!parameters.TryGetValue(parameter.Name, out object? value))
                {
                    if (!parameter.IsOptional)
                    {
                        throw PlatformFault.Create(MissingRequiredField, $"Required field '{parameter.Name}' is missing for RequestName='{api}'");
                    }

                    continue;
                }

                Type? expected = ParameterClrType(parameter);
                if (value != null && expected != null && !expected.IsInstanceOfType(value))
                {
                    throw PlatformFault.Create(InvalidArgument,
                        $"Input field type '{value.GetType().Name}' does not match expected type '{expected.Name}' for field '{parameter.Name}'");
                }
            }

            if (!bound)
            {
                return (string.Empty, Guid.Empty);
            }

            if (!parameters.TryGetValue("Target", out object? target) || target == null)
            {
                throw PlatformFault.Create(MissingRequiredField, $"Required field 'Target' is missing for RequestName='{api}'");
            }

            if (target is not EntityReference reference)
            {
                throw PlatformFault.Create(InvalidArgument,
                    $"Input field type '{target.GetType().Name}' does not match expected type 'EntityReference' for field 'Target'");
            }

            if (!string.Equals(reference.LogicalName, definition.BoundEntityLogicalName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidPluginExecutionException(
                    $"Custom API '{api}' is bound to '{definition.BoundEntityLogicalName}', but its Target is a '{reference.LogicalName}'.");
            }

            return (definition.BoundEntityLogicalName!, reference.Id);
        }

        /// <summary>
        /// The primary entity a Custom API's main operation sees on Dataverse (verified live): "none" and an empty id
        /// for a global API, the bound entity and the Target's id for an entity-bound API, and the bound entity with an
        /// empty id for an entity-collection-bound API.
        /// </summary>
        public static (string Name, Guid Id) PrimaryEntity(CustomApiDefinition? definition, ParameterCollection parameters)
        {
            if (definition == null || definition.BindingType == CustomApiBindingType.Global || string.IsNullOrEmpty(definition.BoundEntityLogicalName))
            {
                return ("none", Guid.Empty);
            }

            Guid id = definition.BindingType == CustomApiBindingType.Entity
                && parameters.TryGetValue("Target", out object? target) && target is EntityReference reference
                ? reference.Id
                : Guid.Empty;
            return (definition.BoundEntityLogicalName!, id);
        }

        // The CLR type a declared parameter must carry, or null for a type name the harness does not know (no check).
        private static Type? ParameterClrType(CustomApiParameterDefinition parameter)
        {
            if (!ParameterTypes.TryGetValue(parameter.ParameterTypeName ?? string.Empty, out Type? type))
            {
                return null;
            }

            return parameter.IsCollection && type == typeof(string) ? typeof(string[]) : type;
        }
    }
}
