using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Constants;

namespace Vela.Testing
{
    // The read path. Retrieve and RetrieveMultiple are pipeline messages like any other: their registered steps fire
    // (PreValidation, PreOperation, PostOperation), a Pre step that replaces the Target/ColumnSet/Query changes what is
    // read, and a Post step that replaces OutputParameters["BusinessEntity"] / ["BusinessEntityCollection"] (the keys
    // Dataverse uses for these two messages) changes what the caller receives. A read with no registered step goes
    // straight to the store.
    public sealed partial class PluginPipeline
    {
        private const string RetrieveOutput = "BusinessEntity";
        private const string RetrieveMultipleOutput = "BusinessEntityCollection";

        internal Entity ExecuteRetrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            RetrieveRequest request = new RetrieveRequest { Target = new EntityReference(entityName, id), ColumnSet = columnSet };
            return (Entity)ExecuteRetrieveRequest(request, new ParameterCollection()).Results["Entity"];
        }

        internal OrganizationResponse ExecuteRetrieveRequest(RetrieveRequest request, ParameterCollection shared)
        {
            string entityName = request.Target?.LogicalName ?? string.Empty;
            if (!HasSteps(PluginMessages.Retrieve, entityName))
            {
                return _store.Execute(request);
            }

            // The steps see the record id even when the caller addressed it by alternate key.
            Guid id = _store.ResolveReference(request.Target!);
            RetrieveRequest inputs = (RetrieveRequest)CopyRequest(request);
            inputs.Target = new EntityReference(entityName, id);

            return RunOperation<OrganizationResponse>(asyncQueue =>
            {
                // A read is not transactional: IsInTransaction is false at every stage (verified live for a top-level
                // read). A read issued from inside a write's transaction still runs within it (its data and its
                // rollback), but its steps also report false: that nested case was NOT verified live.
                FireStage(ReadInvocation(PluginMessages.Retrieve, PluginStages.PreValidation, entityName, id, inputs.Parameters, null), asyncQueue);
                FireStage(ReadInvocation(PluginMessages.Retrieve, PluginStages.PreOperation, entityName, id, inputs.Parameters, null), asyncQueue);

                Entity entity = (Entity)_store.Execute(inputs).Results["Entity"];
                ParameterCollection outputs = new ParameterCollection { [RetrieveOutput] = entity };
                FireStage(ReadInvocation(PluginMessages.Retrieve, PluginStages.PostOperation, entityName, id, inputs.Parameters, outputs), asyncQueue);

                RetrieveResponse response = new RetrieveResponse();
                response.Results["Entity"] = outputs[RetrieveOutput];
                return response;
            }, shared);
        }

        internal EntityCollection ExecuteRetrieveMultiple(QueryBase query, ParameterCollection? shared = null)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            // Only a pipeline with a RetrieveMultiple step pays for resolving the queried table (FetchXML is parsed).
            string entityName = HasSteps(PluginMessages.RetrieveMultiple, null) ? QueryEntityName(query) : string.Empty;
            if (entityName.Length == 0 || !HasSteps(PluginMessages.RetrieveMultiple, entityName))
            {
                return _store.RetrieveMultiple(query);
            }

            return RunOperation(asyncQueue =>
            {
                ParameterCollection inputs = new ParameterCollection { ["Query"] = RequestValues.Clone(query)! };
                FireStage(ReadInvocation(PluginMessages.RetrieveMultiple, PluginStages.PreValidation, entityName, Guid.Empty, inputs, null), asyncQueue);
                FireStage(ReadInvocation(PluginMessages.RetrieveMultiple, PluginStages.PreOperation, entityName, Guid.Empty, inputs, null), asyncQueue);

                QueryBase effective = inputs["Query"] as QueryBase
                    ?? throw new InvalidPluginExecutionException("RetrieveMultiple: a PreOperation step left InputParameters[\"Query\"] without a query.");
                EntityCollection result = _store.RetrieveMultiple(effective);
                ParameterCollection outputs = new ParameterCollection { [RetrieveMultipleOutput] = result };
                FireStage(ReadInvocation(PluginMessages.RetrieveMultiple, PluginStages.PostOperation, entityName, Guid.Empty, inputs, outputs), asyncQueue);

                return (EntityCollection)outputs[RetrieveMultipleOutput];
            }, shared);
        }

        // Whether an enabled step is registered for the message on this table (or as a table-less step).
        private static StageInvocation ReadInvocation(string message, PluginStages stage, string entityName, Guid entityId, ParameterCollection inputs, ParameterCollection? outputs)
        {
            StageInvocation invocation = GenericInvocation(message, stage, entityName, entityId, inputs, outputs);
            invocation.OutsideTransaction = true;
            return invocation;
        }

        // A null entity name asks whether the message has any step at all.
        private bool HasSteps(string message, string? entityName)
        {
            return _registrations.Any(registration => registration.Step.Enabled
                && string.Equals(registration.Step.Message, message, StringComparison.OrdinalIgnoreCase)
                && (entityName == null
                    || string.IsNullOrEmpty(registration.Step.EntityLogicalName)
                    || string.Equals(registration.Step.EntityLogicalName, entityName, StringComparison.OrdinalIgnoreCase)));
        }

        private static string QueryEntityName(QueryBase query)
        {
            switch (query)
            {
                case QueryExpression expression:
                    return expression.EntityName ?? string.Empty;
                case QueryByAttribute byAttribute:
                    return byAttribute.EntityName ?? string.Empty;
                case FetchExpression fetch:
                    return FetchXmlParser.Parse(fetch.Query).Query.EntityName ?? string.Empty;
                default:
                    return string.Empty;
            }
        }
    }
}
