using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.CustomApi;
using Vela.Steps;

namespace Vela.Testing
{
    // Custom APIs. The request is validated against the definition the way the platform does before anything runs
    // (required inputs, input types, the bound Target), then the message runs the full pipeline: PreValidation (before
    // the transaction), PreOperation, the Custom API's own plugin as the MainOperation stage, and PostOperation. Steps
    // registered on the message fire only as far as the API's AllowedCustomProcessingStepType allows. A bound API's
    // context carries the bound table and the Target id as PrimaryEntityName / PrimaryEntityId.
    public sealed partial class PluginPipeline
    {
        private readonly Dictionary<Type, IPlugin> _customApiInstances = new Dictionary<Type, IPlugin>();

        private OrganizationResponse ExecuteCustomApi(OrganizationRequest request, Type pluginType, ParameterCollection shared)
        {
            // One instance per Custom API, reused across calls like the platform's cached plugin instances (the main
            // operation plugin takes no configuration).
            if (!_customApiInstances.TryGetValue(pluginType, out IPlugin? plugin))
            {
                plugin = PluginActivator.Create(PluginActivator.RequireConfigurationConstructor(pluginType, "to run as a Custom API"), null, null);
                _customApiInstances[pluginType] = plugin;
            }

            CustomApiDefinition? definition = (plugin as CustomApiBase)?.GetCustomApiDefinition();
            string message = request.RequestName;

            (string entityName, Guid entityId) = definition != null ? ValidateCustomApiRequest(request, definition) : (string.Empty, Guid.Empty);
            if (definition != null)
            {
                EnsureCustomProcessingStepsAllowed(definition);
            }

            OrganizationRequest inputs = CopyRequest(request);
            (string Name, Guid Id) mainEntity = CustomApiPrimaryEntity(definition, request);

            return RunOperation(asyncQueue =>
            {
                FireStage(GenericInvocation(message, PluginStages.PreValidation, entityName, entityId, inputs.Parameters, null), asyncQueue);
                EnterTransaction();
                FireStage(GenericInvocation(message, PluginStages.PreOperation, entityName, entityId, inputs.Parameters, null), asyncQueue);

                FakePluginExecutionContext context = new FakePluginExecutionContext
                {
                    MessageName = message,
                    Stage = (int)PluginStages.MainOperation,
                    Mode = (int)ExecutionMode.Synchronous,
                    Depth = _depth,
                    // Verified live: "none" for a global API, the bound table (and the Target id) for a bound one.
                    PrimaryEntityName = mainEntity.Name,
                    PrimaryEntityId = mainEntity.Id,
                    CorrelationId = _correlationId,
                    SharedVariables = _sharedVariables,
                    // The main operation runs under a step the platform creates for the API.
                    OwningExtension = new EntityReference("sdkmessageprocessingstep", Security.DeterministicGuid.From("customapi", message))
                    {
                        Name = $"CustomApi '{message}' implementation",
                    },
                };

                foreach (KeyValuePair<string, object> parameter in inputs.Parameters)
                {
                    context.InputParameters[parameter.Key] = parameter.Value;
                }


                _log.Add(new PipelineExecutionEntry(pluginType.FullName ?? pluginType.Name, message, PluginStages.MainOperation, ExecutionMode.Synchronous, entityName, _depth));
                ExecuteWithContext(context, plugin);

                OrganizationResponse response = new OrganizationResponse { ResponseName = message };
                foreach (KeyValuePair<string, object> output in context.OutputParameters)
                {
                    response.Results[output.Key] = output.Value;
                }

                FireStage(GenericInvocation(message, PluginStages.PostOperation, entityName, entityId, inputs.Parameters, response.Results), asyncQueue);
                return response;
            }, shared);
        }

        // The platform's request-validation faults (verified live, raised before any step runs, PreValidation included).
        private const int InvalidArgument = unchecked((int)0x80040315);
        private const int MissingRequiredField = unchecked((int)0x80040203);

        // Request parameters every message accepts besides its own (the optional parameters of the SDK).
        private static readonly HashSet<string> OptionalRequestParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tag", "BypassCustomPluginExecution", "BypassBusinessLogicExecution", "BypassBusinessLogicExecutionStepIds",
            "SuppressDuplicateDetection", "SolutionUniqueName", "MaintainLegacyAppServerBehavior", "SuppressCallbackRegistrationExpanderJob",
            "partitionId",
        };

        // Rejects what the platform rejects before the Custom API runs: a missing required input, an input of the
        // wrong type, an undeclared input and (bound API) a missing or wrong-table Target. Returns the primary entity.
        private static (string EntityName, Guid EntityId) ValidateCustomApiRequest(OrganizationRequest request, CustomApiDefinition definition)
        {
            string api = request.RequestName;
            bool bound = definition.BindingType == CustomApiBindingType.Entity && !string.IsNullOrEmpty(definition.BoundEntityLogicalName);

            foreach (KeyValuePair<string, object> supplied in request.Parameters)
            {
                bool declared = definition.InputParameters.Any(p => string.Equals(p.Name, supplied.Key, StringComparison.Ordinal))
                    || (bound && string.Equals(supplied.Key, "Target", StringComparison.Ordinal))
                    || OptionalRequestParameters.Contains(supplied.Key);
                if (!declared)
                {
                    throw PlatformFault(InvalidArgument, $"Unrecognized request parameter: {supplied.Key}");
                }
            }

            foreach (CustomApiParameterDefinition parameter in definition.InputParameters)
            {
                if (!request.Parameters.TryGetValue(parameter.Name, out object? value))
                {
                    if (!parameter.IsOptional)
                    {
                        throw PlatformFault(MissingRequiredField, $"Required field '{parameter.Name}' is missing for RequestName='{api}'");
                    }

                    continue;
                }

                Type? expected = ParameterClrType(parameter);
                if (value != null && expected != null && !expected.IsInstanceOfType(value))
                {
                    throw PlatformFault(InvalidArgument,
                        $"Input field type '{value.GetType().Name}' does not match expected type '{expected.Name}' for field '{parameter.Name}'");
                }
            }

            if (!bound)
            {
                return (string.Empty, Guid.Empty);
            }

            if (!request.Parameters.TryGetValue("Target", out object? target) || target == null)
            {
                throw PlatformFault(MissingRequiredField, $"Required field 'Target' is missing for RequestName='{api}'");
            }

            if (target is not EntityReference reference)
            {
                throw PlatformFault(InvalidArgument,
                    $"Input field type '{target.GetType().Name}' does not match expected type 'EntityReference' for field 'Target'");
            }

            if (!string.Equals(reference.LogicalName, definition.BoundEntityLogicalName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidPluginExecutionException(
                    $"Custom API '{api}' is bound to '{definition.BoundEntityLogicalName}', but its Target is a '{reference.LogicalName}'.");
            }

            return (definition.BoundEntityLogicalName!, reference.Id);
        }

        private static System.ServiceModel.FaultException<OrganizationServiceFault> PlatformFault(int errorCode, string message)
            => new System.ServiceModel.FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = errorCode, Message = message },
                new System.ServiceModel.FaultReason(message));

        // A step registered on the Custom API's message fires only if the API allows it: None allows no step, AsyncOnly
        // allows asynchronous steps only. The platform refuses to register such a step; the harness fails the call
        // naming it, so a test cannot rely on a step that could never be deployed.
        private void EnsureCustomProcessingStepsAllowed(CustomApiDefinition definition)
        {
            foreach (PipelineRegistration registration in _registrations)
            {
                StepDefinition step = registration.Step;
                if (!step.Enabled || !string.Equals(step.Message, definition.UniqueName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool allowed = definition.AllowedCustomProcessingStepType == CustomApiAllowedCustomProcessingStepType.SyncAndAsync
                    || (definition.AllowedCustomProcessingStepType == CustomApiAllowedCustomProcessingStepType.AsyncOnly
                        && step.Mode == ExecutionMode.Asynchronous);
                if (!allowed)
                {
                    throw new InvalidOperationException(
                        $"Step '{step.PluginTypeName}' ({step.Mode} {step.Stage}) is registered on Custom API '{definition.UniqueName}', " +
                        $"whose AllowedCustomProcessingStepType is {definition.AllowedCustomProcessingStepType}. Dataverse does not accept this step; " +
                        "allow it with AllowedCustomProcessingSteps(...) on the Custom API.");
                }
            }
        }

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
