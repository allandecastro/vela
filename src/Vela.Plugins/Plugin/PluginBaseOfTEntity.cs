using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vela.Context;
using Vela.StepBuilder;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using System.Security.Cryptography;
using System.Text;
using Vela.Constants;

namespace Vela.Plugin
{
    /// <summary>
    /// Generic plugin base class with fluent step configuration and strongly-typed entity access.
    /// </summary>
    /// <typeparam name="TEntity">The early-bound entity type this plugin operates on.</typeparam>
    /// <remarks>
    /// Initializes a new instance of the <see cref="EntityPluginBase{TEntity}"/> class.
    /// </remarks>
    /// <param name="unsecureConfig">Unsecure JSON configuration.</param>
    /// <param name="secureConfig">Secure JSON configuration.</param>
    /// <param name="maxDepth">Depth guard threshold.</param>
    public abstract class EntityPluginBase<TEntity>(string unsecureConfig, string secureConfig, int maxDepth = 8) : PluginBase(unsecureConfig, secureConfig, maxDepth) where TEntity : Entity
    {
        private volatile StepDefinition[]? _stepDefinitions;
        private List<StepBuilder<TEntity>>? _registeredSteps;
        private PluginStepsRegistrar<TEntity>? _steps;
        private readonly object _stepInitLock = new object();

        /// <summary>
        /// Gets all step definitions for this plugin (used for registration manifest generation).
        /// </summary>
        public StepDefinition[] GetStepDefinitions()
        {
            StepDefinition[]? snapshot = _stepDefinitions;
            if (snapshot != null)
            {
                return snapshot;
            }

            // The Dataverse sandbox caches and concurrently reuses a single plugin instance, so the one-time
            // build below — which mutates the shared _steps/_registeredSteps fields — must be serialized.
            // Otherwise two concurrent first executions race and corrupt each other's step lists.
            lock (_stepInitLock)
            {
                if (_stepDefinitions != null)
                {
                    return _stepDefinitions;
                }

                List<StepDefinition> definitions = new List<StepDefinition>();
                List<StepBuilder<TEntity>> registeredSteps = new List<StepBuilder<TEntity>>();
                _registeredSteps = registeredSteps;
                _steps = new PluginStepsRegistrar<TEntity>(RegisterFluentStep);
                try
                {
                    RegisterPluginSteps();
                }
                finally
                {
                    _steps = null;
                    _registeredSteps = null;
                }

                definitions.AddRange(registeredSteps.Select(step => step.Build()));
                if (definitions.Count == 0)
                {
                    throw new InvalidPluginExecutionException($"Plugin '{GetType().FullName}' must register at least one step.");
                }

                string pluginTypeName = GetType().FullName ?? GetType().Name;
                foreach (StepDefinition definition in definitions)
                {
                    definition.PluginTypeName = pluginTypeName;
                    if (string.IsNullOrWhiteSpace(definition.StepKey))
                    {
                        definition.StepKey = GenerateAutoStepKey(definition);
                    }
                }

                if (definitions.Count > 1)
                {
                    // Two steps with the same trigger (message + stage + mode + entity) can't be told apart at
                    // runtime: ResolveSingleMatchingStep would find both and throw, and at registration their
                    // auto step-keys collide so one silently overwrites the other. Fail fast with a clear message.
                    HashSet<string> triggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (StepDefinition definition in definitions)
                    {
                        string trigger = string.Join("|",
                            definition.Message ?? string.Empty,
                            (int)definition.Stage,
                            (int)definition.Mode,
                            definition.EntityLogicalName ?? string.Empty);
                        if (!triggers.Add(trigger))
                        {
                            throw new InvalidPluginExecutionException(
                                $"Plugin '{GetType().FullName}' registers more than one step for the same trigger " +
                                $"(message '{definition.Message}', stage {(int)definition.Stage}, mode {(int)definition.Mode}). " +
                                "Merge them into a single step (combine the filtering attributes) or use a different stage/mode.");
                        }
                    }

                    for (int i = 0; i < definitions.Count; i++)
                    {
                        if (string.IsNullOrWhiteSpace(definitions[i].HandlerMethodName))
                        {
                            throw new InvalidPluginExecutionException(
                                $"Plugin '{GetType().FullName}' step at index {i} is missing Handler(...).");
                        }
                    }
                }

                StepDefinition[] result = definitions.ToArray();
                _stepDefinitions = result;
                return result;
            }
        }

        /// <summary>
        /// Gets the first step definition for this plugin.
        /// </summary>
        public StepDefinition GetStepDefinition()
        {
            return GetStepDefinitions()[0];
        }

        /// <summary>
        /// Fluent step registration entry point for <see cref="RegisterPluginSteps"/>.
        /// </summary>
        protected IStepBuilder<TEntity> RegisterSteps()
        {
            return _steps ?? throw new InvalidOperationException(
                $"Plugin '{GetType().FullName}' can only call RegisterSteps() inside RegisterPluginSteps().");
        }

        /// <summary>
        /// Configure all plugin steps for this plugin.
        /// </summary>
        protected virtual void RegisterPluginSteps()
        {
        }

        /// <summary>
        /// Execute the plugin business logic for a single-step plugin. The message-specific request and
        /// response are available through <paramref name="ctx"/> (for example <c>ctx.GetTarget&lt;TEntity&gt;()</c>
        /// or <c>ctx.GetRequest&lt;UpdateRequest&gt;()</c>). Multi-step plugins bind a typed handler per step
        /// instead of overriding this method.
        /// </summary>
        /// <param name="ctx">The local plugin context.</param>
        protected virtual void Execute(ILocalPluginContext ctx)
        {
            throw new InvalidPluginExecutionException(
                $"Plugin '{GetType().FullName}' must override Execute(ILocalPluginContext) or configure handlers in RegisterPluginSteps().");
        }

        /// <summary>
        /// Routes the current pipeline execution to the matching typed step handler, or to the single-step
        /// <see cref="Execute(ILocalPluginContext)"/> override when no handler is bound.
        /// </summary>
        /// <param name="ctx">Local plugin context.</param>
        protected sealed override void ExecuteDataversePlugin(ILocalPluginContext ctx)
        {
            StepDefinition[] definitions = GetStepDefinitions();
            StepDefinition? matchedDefinition = ResolveSingleMatchingStep(ctx, definitions);

            if (matchedDefinition != null && !string.IsNullOrWhiteSpace(matchedDefinition.HandlerMethodName))
            {
                // Every handler takes a single strongly-typed argument. Resolution order matters when a typed
                // request's name collides with a modelled message (e.g. On<MergeRequest>() resolves to "Merge"):
                // that handler's parameter is IMessagePre/PostContext<TRequest>, which the modelled
                // MergePreContext<TEntity> does NOT implement, so passing the modelled context throws. The generic
                // message context (built from the handler's own parameter type) is therefore tried first; it
                // returns null for a modelled handler (e.g. IMergeContext<TEntity>) and for the untyped permissive
                // path, falling back to the per-message modelled context, then to the plain ILocalPluginContext.
                object argument = BuildGenericMessageContext(matchedDefinition.HandlerMethodName!, ctx)
                    ?? BuildTypedContext(matchedDefinition.Message, matchedDefinition.Stage, ctx)
                    ?? ctx;
                InvokeTypedHandler(matchedDefinition.HandlerMethodName!, argument);
                return;
            }

            if (definitions.Length > 1)
            {
                throw new InvalidPluginExecutionException(
                    $"Plugin '{GetType().FullName}' has multiple steps and requires a handler for each matching step.");
            }

            Execute(ctx);
        }

        private static string GenerateAutoStepKey(StepDefinition definition)
        {
            string seed = string.Join("|",
                definition.PluginTypeName ?? string.Empty,
                definition.Message ?? string.Empty,
                (int)definition.Stage,
                (int)definition.Mode,
                definition.EntityLogicalName ?? string.Empty,
                definition.ExecutionOrder);

            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
            string hex = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            return "dv-" + hex.Substring(0, 12);
        }

        private void RegisterFluentStep(StepBuilder<TEntity> stepBuilder)
        {
            if (stepBuilder == null)
            {
                throw new ArgumentNullException(nameof(stepBuilder));
            }

            if (_registeredSteps == null)
            {
                throw new InvalidOperationException(
                    $"Plugin '{GetType().FullName}' can only register steps inside RegisterPluginSteps().");
            }

            _registeredSteps.Add(stepBuilder);
        }

        private StepDefinition? ResolveSingleMatchingStep(ILocalPluginContext ctx, StepDefinition[] definitions)
        {
            List<StepDefinition> matches = new List<StepDefinition>();
            foreach (StepDefinition definition in definitions)
            {
                if (!string.Equals(definition.Message, ctx.MessageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((int)definition.Stage != ctx.Stage || (int)definition.Mode != ctx.Mode)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(definition.EntityLogicalName) &&
                    !string.Equals(definition.EntityLogicalName, ctx.PrimaryEntityName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches.Add(definition);
            }

            if (matches.Count == 0)
            {
                return null;
            }

            if (matches.Count > 1)
            {
                throw new InvalidPluginExecutionException(
                    $"Plugin '{GetType().FullName}' has {matches.Count} matching step definitions for the current pipeline execution.");
            }

            return matches[0];
        }

        // Messages Vela models with a typed context: build the per-message context so its handler receives a
        // single strongly-typed argument (e.g. IMergeContext<TEntity>). Returns null for a permissive
        // OnMessage(...) step that Vela does not model, so its handler receives the plain ILocalPluginContext.
        private static object? BuildTypedContext(string? message, PluginStages stage, ILocalPluginContext ctx)
        {
            bool post = stage == PluginStages.PostOperation;
            return message switch
            {
                PluginMessages.Create => post ? (object)new CreatePostContext<TEntity>(ctx) : new CreatePreContext<TEntity>(ctx),
                PluginMessages.Update => post ? (object)new UpdatePostContext<TEntity>(ctx) : new UpdatePreContext<TEntity>(ctx),
                PluginMessages.Delete => post ? (object)new DeletePostContext<TEntity>(ctx) : new DeletePreContext<TEntity>(ctx),
                PluginMessages.CreateMultiple => post ? (object)new CreateMultiplePostContext<TEntity>(ctx) : new CreateMultiplePreContext<TEntity>(ctx),
                PluginMessages.UpdateMultiple => post ? (object)new UpdateMultiplePostContext<TEntity>(ctx) : new UpdateMultiplePreContext<TEntity>(ctx),
                PluginMessages.Retrieve => post ? (object)new RetrievePostContext<TEntity>(ctx) : new RetrievePreContext<TEntity>(ctx),
                PluginMessages.RetrieveMultiple => post ? (object)new RetrieveMultiplePostContext<TEntity>(ctx) : new RetrieveMultiplePreContext<TEntity>(ctx),
                PluginMessages.Associate => post ? (object)new AssociatePostContext<TEntity>(ctx) : new AssociatePreContext<TEntity>(ctx),
                PluginMessages.Disassociate => post ? (object)new DisassociatePostContext<TEntity>(ctx) : new DisassociatePreContext<TEntity>(ctx),
                PluginMessages.Merge => post ? (object)new MergePostContext<TEntity>(ctx) : new MergePreContext<TEntity>(ctx),
                PluginMessages.GrantAccess => post ? (object)new GrantAccessPostContext<TEntity>(ctx) : new GrantAccessPreContext<TEntity>(ctx),
                PluginMessages.ModifyAccess => post ? (object)new ModifyAccessPostContext<TEntity>(ctx) : new ModifyAccessPreContext<TEntity>(ctx),
                PluginMessages.RevokeAccess => post ? (object)new RevokeAccessPostContext<TEntity>(ctx) : new RevokeAccessPreContext<TEntity>(ctx),
                PluginMessages.QualifyLead => post ? (object)new QualifyLeadPostContext<TEntity>(ctx) : new QualifyLeadPreContext<TEntity>(ctx),
                PluginMessages.WinOpportunity => post ? (object)new WinOpportunityPostContext<TEntity>(ctx) : new WinOpportunityPreContext<TEntity>(ctx),
                PluginMessages.LoseOpportunity => post ? (object)new LoseOpportunityPostContext<TEntity>(ctx) : new LoseOpportunityPreContext<TEntity>(ctx),
                PluginMessages.ConvertQuoteToSalesOrder => post ? (object)new ConvertQuoteToSalesOrderPostContext<TEntity>(ctx) : new ConvertQuoteToSalesOrderPreContext<TEntity>(ctx),
                PluginMessages.ConvertSalesOrderToInvoice => post ? (object)new ConvertSalesOrderToInvoicePostContext<TEntity>(ctx) : new ConvertSalesOrderToInvoicePreContext<TEntity>(ctx),
                PluginMessages.GenerateQuoteFromOpportunity => post ? (object)new GenerateQuoteFromOpportunityPostContext<TEntity>(ctx) : new GenerateQuoteFromOpportunityPreContext<TEntity>(ctx),
                PluginMessages.GenerateSalesOrderFromOpportunity => post ? (object)new GenerateSalesOrderFromOpportunityPostContext<TEntity>(ctx) : new GenerateSalesOrderFromOpportunityPreContext<TEntity>(ctx),
                PluginMessages.GenerateInvoiceFromOpportunity => post ? (object)new GenerateInvoiceFromOpportunityPostContext<TEntity>(ctx) : new GenerateInvoiceFromOpportunityPreContext<TEntity>(ctx),
                PluginMessages.AddToQueue => post ? (object)new AddToQueuePostContext<TEntity>(ctx) : new AddToQueuePreContext<TEntity>(ctx),
                PluginMessages.RemoveFromQueue => post ? (object)new RemoveFromQueuePostContext<TEntity>(ctx) : new RemoveFromQueuePreContext<TEntity>(ctx),
                PluginMessages.PickFromQueue => post ? (object)new PickFromQueuePostContext<TEntity>(ctx) : new PickFromQueuePreContext<TEntity>(ctx),
                PluginMessages.RouteTo => post ? (object)new RouteToPostContext<TEntity>(ctx) : new RouteToPreContext<TEntity>(ctx),
                PluginMessages.ReleaseToQueue => post ? (object)new ReleaseToQueuePostContext<TEntity>(ctx) : new ReleaseToQueuePreContext<TEntity>(ctx),
                PluginMessages.SendEmail => post ? (object)new SendEmailPostContext<TEntity>(ctx) : new SendEmailPreContext<TEntity>(ctx),
                PluginMessages.SendEmailFromTemplate => post ? (object)new SendEmailFromTemplatePostContext<TEntity>(ctx) : new SendEmailFromTemplatePreContext<TEntity>(ctx),
                PluginMessages.DeliverIncomingEmail => post ? (object)new DeliverIncomingEmailPostContext<TEntity>(ctx) : new DeliverIncomingEmailPreContext<TEntity>(ctx),
                PluginMessages.DeliverPromoteEmail => post ? (object)new DeliverPromoteEmailPostContext<TEntity>(ctx) : new DeliverPromoteEmailPreContext<TEntity>(ctx),
                PluginMessages.AddMembersTeam => post ? (object)new AddMembersTeamPostContext<TEntity>(ctx) : new AddMembersTeamPreContext<TEntity>(ctx),
                PluginMessages.RemoveMembersTeam => post ? (object)new RemoveMembersTeamPostContext<TEntity>(ctx) : new RemoveMembersTeamPreContext<TEntity>(ctx),
                PluginMessages.AddListMembersList => post ? (object)new AddListMembersListPostContext<TEntity>(ctx) : new AddListMembersListPreContext<TEntity>(ctx),
                PluginMessages.AddMemberList => post ? (object)new AddMemberListPostContext<TEntity>(ctx) : new AddMemberListPreContext<TEntity>(ctx),
                PluginMessages.RemoveMemberList => post ? (object)new RemoveMemberListPostContext<TEntity>(ctx) : new RemoveMemberListPreContext<TEntity>(ctx),
                PluginMessages.CloseIncident => post ? (object)new CloseIncidentPostContext<TEntity>(ctx) : new CloseIncidentPreContext<TEntity>(ctx),
                PluginMessages.CancelContract => post ? (object)new CancelContractPostContext<TEntity>(ctx) : new CancelContractPreContext<TEntity>(ctx),
                PluginMessages.CloneContract => post ? (object)new CloneContractPostContext<TEntity>(ctx) : new CloneContractPreContext<TEntity>(ctx),
                PluginMessages.RetrievePrincipalAccess => post ? (object)new RetrievePrincipalAccessPostContext<TEntity>(ctx) : new RetrievePrincipalAccessPreContext<TEntity>(ctx),
                PluginMessages.RetrieveSharedPrincipalsAndAccess => post ? (object)new RetrieveSharedPrincipalsAndAccessPostContext<TEntity>(ctx) : new RetrieveSharedPrincipalsAndAccessPreContext<TEntity>(ctx),
                PluginMessages.WinQuote => post ? (object)new WinQuotePostContext<TEntity>(ctx) : new WinQuotePreContext<TEntity>(ctx),
                PluginMessages.ReviseQuote => post ? (object)new ReviseQuotePostContext<TEntity>(ctx) : new ReviseQuotePreContext<TEntity>(ctx),
                PluginMessages.FulfillSalesOrder => post ? (object)new FulfillSalesOrderPostContext<TEntity>(ctx) : new FulfillSalesOrderPreContext<TEntity>(ctx),
                PluginMessages.GetQuoteProductsFromOpportunity => post ? (object)new GetQuoteProductsFromOpportunityPostContext<TEntity>(ctx) : new GetQuoteProductsFromOpportunityPreContext<TEntity>(ctx),
                PluginMessages.GetSalesOrderProductsFromOpportunity => post ? (object)new GetSalesOrderProductsFromOpportunityPostContext<TEntity>(ctx) : new GetSalesOrderProductsFromOpportunityPreContext<TEntity>(ctx),
                PluginMessages.GetInvoiceProductsFromOpportunity => post ? (object)new GetInvoiceProductsFromOpportunityPostContext<TEntity>(ctx) : new GetInvoiceProductsFromOpportunityPreContext<TEntity>(ctx),
                _ => null,
            };
        }

        // A permissive OnMessage(...) step whose handler takes a generic message context
        // (IMessagePreContext<TRequest> / IMessagePostContext<TRequest>) does not carry TRequest in the message
        // name, so it is resolved from the handler method's single parameter type. The matching concrete context
        // (MessagePreContext<TRequest> / MessagePostContext<TRequest>) is then built over the live pipeline
        // context. Returns null when the handler takes the plain ILocalPluginContext (the untyped permissive path),
        // so dispatch falls back to passing ctx.
        private object? BuildGenericMessageContext(string methodName, ILocalPluginContext ctx)
        {
            foreach (MethodInfo method in HandlerResolver.GetNamedMethods(GetType(), methodName))
            {
                if (method.ReturnType != typeof(void))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1)
                {
                    continue;
                }

                Type parameterType = parameters[0].ParameterType;
                if (!parameterType.IsGenericType)
                {
                    continue;
                }

                // Disambiguate by the parameter's own generic type definition. A two-arg entity-typed context
                // (IMessagePreContext<TRequest, TEntity>) is preferred, and it is matched here directly because
                // the parameter type is that two-arg interface - even though it also implements the one-arg
                // IMessagePreContext<TRequest>, the parameter's GetGenericTypeDefinition() reports the two-arg form.
                Type definition = parameterType.GetGenericTypeDefinition();
                Type contextDefinition;
                Type[] parameterArguments = parameterType.GetGenericArguments();
                if (definition == typeof(IMessagePreContext<,>))
                {
                    contextDefinition = typeof(MessagePreContext<,>);
                }
                else if (definition == typeof(IMessagePostContext<,>))
                {
                    contextDefinition = typeof(MessagePostContext<,>);
                }
                else if (definition == typeof(IMessagePreContext<>))
                {
                    contextDefinition = typeof(MessagePreContext<>);
                }
                else if (definition == typeof(IMessagePostContext<>))
                {
                    contextDefinition = typeof(MessagePostContext<>);
                }
                else
                {
                    continue;
                }

                Type contextType = contextDefinition.MakeGenericType(parameterArguments);
                return Activator.CreateInstance(contextType, ctx);
            }

            return null;
        }

        private void InvokeTypedHandler(string methodName, object typedContext)
        {
            Type contextType = typedContext.GetType();
            HandlerInvoker.ResolveAndInvoke(
                this,
                methodName,
                method => HandlerSignature.IsSupported(method, parameterType => parameterType.IsAssignableFrom(contextType)),
                [typedContext],
                subjectLabel: $"Plugin '{GetType().FullName}'",
                handlerNoun: "step handler",
                expectedSignature: $"void {methodName}(a typed message context)");
        }
    }
}
