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

            // The extra steps see the primary entity of the binding, as the main operation does (verified live at s10,
            // s20, s40 sync and s40 async): "none" and an empty id for a global API, the bound table with the Target id
            // (entity-bound) or an empty id (collection-bound). An asynchronous step's id is always empty.
            (string stepEntity, Guid stepId) = definition != null ? mainEntity : (entityName, entityId);

            return RunOperation(asyncQueue => RunStages(
                stage => FireStage(CustomApiInvocation(message, stage, stepEntity, stepId, inputs.Parameters, null), asyncQueue),
                () => ExecuteCustomApiMain(plugin, pluginType, message, entityName, mainEntity, inputs),
                response => FireStage(CustomApiInvocation(message, PluginStages.PostOperation, stepEntity, stepId, inputs.Parameters, response.Results), asyncQueue)),
                shared);
        }

        private static StageInvocation CustomApiInvocation(string message, PluginStages stage, string entityName, Guid entityId, ParameterCollection inputs, ParameterCollection? outputs)
        {
            StageInvocation invocation = GenericInvocation(message, stage, entityName, entityId, inputs, outputs);
            invocation.AsyncDropsPrimaryId = true;
            return invocation;
        }

        // The Custom API's main operation: its plugin runs under the step the platform creates for the API, and its
        // OutputParameters become the response.
        private OrganizationResponse ExecuteCustomApiMain(IPlugin plugin, Type pluginType, string message, string entityName, (string Name, Guid Id) mainEntity, OrganizationRequest inputs)
        {
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

            return response;
        }

        // A step registered on the Custom API's message fires only if the API allows it: None allows no step, AsyncOnly
        // allows asynchronous steps only. The platform refuses to register such a step; the harness fails the call
        // naming it, so a test cannot rely on a step that could never be deployed. The same goes for a step's table on a
        // bound API: the API has one message filter (verified live), on its bound table, so a step on another table
        // cannot be registered (vela plan blocks it). A global API's steps register without a table and run whatever
        // their plugin's table.
        private void EnsureCustomProcessingStepsAllowed(CustomApiDefinition definition)
        {
            foreach (PipelineRegistration registration in _registrations)
            {
                StepDefinition step = registration.Step;
                if (!step.Enabled || !string.Equals(step.Message, definition.UniqueName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CustomApiStepTableProblem(definition, step.EntityLogicalName) is string tableProblem)
                {
                    throw new InvalidOperationException($"Step '{step.PluginTypeName}' ({step.Mode} {step.Stage}): {tableProblem}");
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

        // Why a step declared on this table cannot be registered on the Custom API's message, or null when it can. A
        // global API's only filter has no table: vela plan registers its steps without their plugin's table, and they
        // run on "none" whatever that table is, as here. A bound API's only filter is on its bound table.
        private static string? CustomApiStepTableProblem(CustomApiDefinition definition, string? stepTable)
        {
            bool global = definition.BindingType == CustomApiBindingType.Global || string.IsNullOrEmpty(definition.BoundEntityLogicalName);
            if (string.IsNullOrEmpty(stepTable) || global
                || string.Equals(stepTable, definition.BoundEntityLogicalName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return $"Custom API '{definition.UniqueName}' is bound to '{definition.BoundEntityLogicalName}': its only message filter is on that " +
                $"table, so a step on it cannot be registered on '{stepTable}' (vela plan refuses it).";
        }
    }
}
