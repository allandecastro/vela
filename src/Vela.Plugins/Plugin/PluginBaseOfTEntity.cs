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
        /// Execute the plugin business logic with a strongly-typed target entity.
        /// </summary>
        /// <param name="ctx">The local plugin context.</param>
        /// <param name="target">The target entity (from InputParameters["Target"]).</param>
        protected virtual void Execute(ILocalPluginContext ctx, TEntity target)
        {
            throw new InvalidPluginExecutionException(
                $"Plugin '{GetType().FullName}' must override Execute(ILocalPluginContext, {typeof(TEntity).Name}) or configure handlers in RegisterPluginSteps().");
        }

        /// <summary>
        /// Executes the plugin after extracting the strongly-typed target.
        /// </summary>
        /// <param name="ctx">Local plugin context.</param>
        protected sealed override void ExecuteDataversePlugin(ILocalPluginContext ctx)
        {
            TEntity target = ResolveTarget(ctx);
            StepDefinition[] definitions = GetStepDefinitions();
            StepDefinition? matchedDefinition = ResolveSingleMatchingStep(ctx, definitions);

            if (matchedDefinition != null && !string.IsNullOrWhiteSpace(matchedDefinition.HandlerMethodName))
            {
                // A single-argument handler is the typed-context form (Action<I{Msg}Context> or
                // Action<ILocalPluginContext>); a two-argument handler is the legacy (ctx, target) form. Branch
                // on the resolved method's arity so both coexist during the transition.
                MethodInfo? method = GetType().GetMethod(
                    matchedDefinition.HandlerMethodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null && method.GetParameters().Length == 1)
                {
                    object argument = BuildTypedContext(matchedDefinition.Message, ctx) ?? ctx;
                    InvokeTypedHandler(matchedDefinition.HandlerMethodName, argument);
                }
                else
                {
                    InvokeHandler(matchedDefinition.HandlerMethodName, ctx, target);
                }
                return;
            }

            if (definitions.Length > 1)
            {
                throw new InvalidPluginExecutionException(
                    $"Plugin '{GetType().FullName}' has multiple steps and requires a handler for each matching step.");
            }

            Execute(ctx, target);
        }

        // Logical name of TEntity, read once from its early-bound attribute (mirrors StepBuilder).
        private static readonly string EntityLogicalName =
            typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>()?.LogicalName
            ?? typeof(TEntity).Name.ToLowerInvariant();

        /// <summary>
        /// Resolves the strongly-typed primary record for the current message. Create/Update carry a full
        /// <see cref="Entity"/> in <c>Target</c>; Delete/Associate/Disassociate/Retrieve carry an
        /// <see cref="EntityReference"/> in <c>Target</c> and SetState an <c>EntityMoniker</c> — for those we
        /// materialize a typed entity holding just its id. Messages with no primary record (e.g.
        /// RetrieveMultiple) yield an empty typed entity, so <see cref="Execute"/> and handlers never see null.
        /// </summary>
        private static TEntity ResolveTarget(ILocalPluginContext ctx)
        {
            ParameterCollection inputs = ctx.PluginExecutionContext.InputParameters;

            if (inputs.TryGetValue("Target", out object? target))
            {
                if (target is Entity entity)
                {
                    return entity.ToEntity<TEntity>();
                }

                if (target is EntityReference targetRef)
                {
                    return FromReference(targetRef);
                }
            }

            if (inputs.TryGetValue("EntityMoniker", out object? moniker) && moniker is EntityReference monikerRef)
            {
                return FromReference(monikerRef);
            }

            return new Entity(EntityLogicalName).ToEntity<TEntity>();
        }

        private static TEntity FromReference(EntityReference reference)
        {
            string logicalName = string.IsNullOrWhiteSpace(reference.LogicalName) ? EntityLogicalName : reference.LogicalName;
            return new Entity(logicalName) { Id = reference.Id }.ToEntity<TEntity>();
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

        private void InvokeHandler(string methodName, ILocalPluginContext ctx, TEntity target)
        {
            HandlerInvoker.ResolveAndInvoke(
                this,
                methodName,
                IsSupportedHandlerSignature,
                [ctx, target],
                subjectLabel: $"Plugin '{GetType().FullName}'",
                handlerNoun: "step handler",
                expectedSignature: $"void {methodName}(ILocalPluginContext, {typeof(TEntity).Name})");
        }

        private static bool IsSupportedHandlerSignature(MethodInfo method)
        {
            return HandlerSignature.IsSupported(
                method,
                contextType => typeof(ILocalPluginContext).IsAssignableFrom(contextType),
                targetType => targetType.IsAssignableFrom(typeof(TEntity)));
        }

        // Messages Vela models with a typed context: build the per-message context so its handler receives a
        // single strongly-typed argument (e.g. IMergeContext<TEntity>). Returns null for messages without a
        // typed context (they use the uniform (ctx, target) handler / Execute).
        private static object? BuildTypedContext(string? message, ILocalPluginContext ctx)
        {
            return message switch
            {
                PluginMessages.Create => new CreateContext<TEntity>(ctx),
                PluginMessages.Update => new UpdateContext<TEntity>(ctx),
                PluginMessages.Delete => new DeleteContext<TEntity>(ctx),
                PluginMessages.CreateMultiple => new CreateMultipleContext<TEntity>(ctx),
                PluginMessages.UpdateMultiple => new UpdateMultipleContext<TEntity>(ctx),
                PluginMessages.Merge => new MergeContext<TEntity>(ctx),
                PluginMessages.Retrieve => new RetrieveContext<TEntity>(ctx),
                PluginMessages.RetrieveMultiple => new RetrieveMultipleContext<TEntity>(ctx),
                PluginMessages.Associate => new AssociateContext<TEntity>(ctx),
                PluginMessages.Disassociate => new DisassociateContext<TEntity>(ctx),
                PluginMessages.GrantAccess => new GrantAccessContext<TEntity>(ctx),
                PluginMessages.ModifyAccess => new ModifyAccessContext<TEntity>(ctx),
                PluginMessages.RevokeAccess => new RevokeAccessContext<TEntity>(ctx),
                _ => null,
            };
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
