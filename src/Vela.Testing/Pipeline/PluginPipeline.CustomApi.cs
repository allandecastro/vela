using System;
using System.Collections.Generic;
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

            (string entityName, Guid entityId) = definition != null ? CustomApiRequestValidator.Validate(request.RequestName, request.Parameters, definition) : (string.Empty, Guid.Empty);
            if (definition != null)
            {
                EnsureCustomProcessingStepsAllowed(definition);
            }

            OrganizationRequest inputs = CopyRequest(request);
            (string Name, Guid Id) mainEntity = CustomApiRequestValidator.PrimaryEntity(definition, request.Parameters);

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

                PipelineExecutionEntry entry = new PipelineExecutionEntry(pluginType.FullName ?? pluginType.Name, message, PluginStages.MainOperation, ExecutionMode.Synchronous, entityName, _depth);
                _log.Add(entry);
                ExecuteWithContext(context, plugin, entry);

                OrganizationResponse response = new OrganizationResponse { ResponseName = message };
                foreach (KeyValuePair<string, object> output in context.OutputParameters)
                {
                    response.Results[output.Key] = output.Value;
                }

                FireStage(GenericInvocation(message, PluginStages.PostOperation, entityName, entityId, inputs.Parameters, response.Results), asyncQueue);
                return response;
            }, shared);
        }

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
    }
}
