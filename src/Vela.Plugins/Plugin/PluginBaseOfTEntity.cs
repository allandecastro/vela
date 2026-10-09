using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vela.Context;
using Vela.Steps;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using System.Security.Cryptography;
using System.Text;
using Vela.Constants;

namespace Vela.Plugin
{
    /// <summary>How a handler parameter type resolves as a derived (generated) step context.</summary>
    internal enum StepContextKind
    {
        /// <summary>Not a class deriving from a Vela step context; another builder should handle it.</summary>
        NotApplicable,

        /// <summary>Derives from a step context and can be constructed via its (ILocalPluginContext) ctor.</summary>
        Constructable,

        /// <summary>Derives from a step context but is abstract, so it cannot be constructed.</summary>
        Abstract,

        /// <summary>Derives from a step context but lacks a public (ILocalPluginContext) constructor.</summary>
        NoConstructor,
    }

    /// <summary>A cached resolution of a handler parameter type to its step-context construction strategy.</summary>
    internal readonly struct StepContextResolution(StepContextKind kind, ConstructorInfo? constructor)
    {
        public StepContextKind Kind { get; } = kind;

        public ConstructorInfo? Constructor { get; } = constructor;
    }

    /// <summary>
    /// Resolves and caches, per handler parameter type, whether it is a generated step context and how to build it.
    /// Non-generic so the cache is shared across every <see cref="EntityPluginBase{TEntity}"/> closure. The cache is
    /// keyed by <see cref="Type"/> (bounded by the loaded types), matching the framework's other reflection caches.
    /// </summary>
    internal static class StepContextFactory
    {
        private static readonly ConcurrentDictionary<Type, StepContextResolution> Cache =
            new ConcurrentDictionary<Type, StepContextResolution>();

        public static StepContextResolution Resolve(Type parameterType) =>
            Cache.GetOrAdd(parameterType, ResolveCore);

        private static StepContextResolution ResolveCore(Type parameterType)
        {
            if (!parameterType.IsClass || !DerivesFromStepContext(parameterType))
            {
                return new StepContextResolution(StepContextKind.NotApplicable, null);
            }

            if (parameterType.IsAbstract)
            {
                return new StepContextResolution(StepContextKind.Abstract, null);
            }

            ConstructorInfo? ctor = parameterType.GetConstructor([typeof(ILocalPluginContext)]);
            return ctor is null
                ? new StepContextResolution(StepContextKind.NoConstructor, null)
                : new StepContextResolution(StepContextKind.Constructable, ctor);
        }

        private static bool DerivesFromStepContext(Type type)
        {
            for (Type? t = type.BaseType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType)
                {
                    Type definition = t.GetGenericTypeDefinition();
                    if (definition == typeof(PreStepContext<>) || definition == typeof(PostStepContext<,>))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Generic plugin base class with fluent step configuration and strongly-typed entity access.
    /// </summary>
    /// <typeparam name="TEntity">The early-bound entity type this plugin operates on.</typeparam>
    /// <remarks>
    /// Initializes a new instance of the <see cref="EntityPluginBase{TEntity}"/> class.
    /// </remarks>
    /// <param name="unsecureConfig">Unsecure JSON configuration.</param>
    /// <param name="secureConfig">Secure JSON configuration.</param>
    /// <param name="maxDepth">Depth guard threshold (defaults to <see cref="PluginBase.DefaultMaxDepth"/>).</param>
    public abstract class EntityPluginBase<TEntity>(string unsecureConfig, string secureConfig, int maxDepth = PluginBase.DefaultMaxDepth) : PluginBase(unsecureConfig, secureConfig, maxDepth) where TEntity : Entity
    {
        private volatile StepDefinition[]? _stepDefinitions;
        // Runtime handler delegates keyed by their (reference-identity) StepDefinition, retained so the matched
        // step can be invoked directly instead of resolving the handler by method name. Built together with
        // _stepDefinitions and published under the same lock; StepDefinition stays a pure metadata value object.
        private Dictionary<StepDefinition, Delegate>? _handlersByDefinition;
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

                // Build each step and, in lockstep, capture its retained runtime handler delegate keyed by the
                // definition instance (Build() returns the builder's single StepDefinition, so reference identity
                // is stable and unique per step).
                Dictionary<StepDefinition, Delegate> handlersByDefinition = new Dictionary<StepDefinition, Delegate>();
                foreach (StepBuilder<TEntity> step in registeredSteps)
                {
                    StepDefinition built = step.Build();
                    definitions.Add(built);
                    if (step.RuntimeHandler != null)
                    {
                        handlersByDefinition[built] = step.RuntimeHandler;
                    }
                }

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
                }

                // Every step must bind a handler with .Handle(...). There is no Execute(ctx) fallback, so a step
                // with no handler has nothing to run; fail fast (the VELA002 analyzer catches this at compile time).
                foreach (StepDefinition definition in definitions)
                {
                    if (string.IsNullOrWhiteSpace(definition.HandlerMethodName))
                    {
                        throw new InvalidPluginExecutionException(
                            $"Plugin '{GetType().FullName}' has a step on message '{definition.Message}' with no handler. " +
                            "End the RegisterStep() chain with .Handle(...).");
                    }
                }

                StepDefinition[] result = definitions.ToArray();
                _handlersByDefinition = handlersByDefinition;
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
        /// Registers one step with the fluent builder:
        /// <c>RegisterStep().On&lt;Message&gt;().&lt;Stage&gt;().&lt;Mode&gt;().With...().Handle(handler)</c>.
        /// Call once per step inside <see cref="RegisterPluginSteps"/>.
        /// </summary>
        protected Vela.Steps.IStepTrigger<TEntity> RegisterStep()
        {
            return _steps ?? throw new InvalidOperationException(
                $"Plugin '{GetType().FullName}' can only call RegisterStep() inside RegisterPluginSteps().");
        }

        /// <summary>
        /// Configure all plugin steps for this plugin.
        /// </summary>
        protected virtual void RegisterPluginSteps()
        {
        }

        /// <summary>
        /// Routes the current pipeline execution to the matching step's typed handler. Every step binds a
        /// handler with <c>.Handle(...)</c> (enforced in <see cref="GetStepDefinitions"/>), so there is no
        /// untyped <c>Execute(ctx)</c> fallback.
        /// </summary>
        /// <param name="ctx">Local plugin context.</param>
        protected sealed override void ExecuteDataversePlugin(ILocalPluginContext ctx)
        {
            StepDefinition[] definitions = GetStepDefinitions();
            StepDefinition? matchedDefinition = ResolveSingleMatchingStep(ctx, definitions);

            if (matchedDefinition == null)
            {
                throw new InvalidPluginExecutionException(
                    $"Plugin '{GetType().FullName}' has no registered step matching message '{ctx.MessageName}'.");
            }

            // Prefer invoking the retained delegate directly: it supports lambda handlers (including ones that
            // capture locals, which the name-based reflection path cannot find) and the typed-image Handle<TImg>
            // wrapper. The context is built from the delegate's own first parameter type, the same rule used for
            // the reflection path below - so guided (modelled context) and typed (generic message context) steps,
            // and the typed-image wrappers, all resolve their context through one path.
            if (_handlersByDefinition != null && _handlersByDefinition.TryGetValue(matchedDefinition, out Delegate? runtimeHandler))
            {
                Type parameterType = runtimeHandler.Method.GetParameters()[0].ParameterType;
                object delegateArgument = BuildDerivedStepContext(parameterType, ctx)
                    ?? BuildGenericMessageContextForType(parameterType, ctx)
                    ?? BuildTypedContext(matchedDefinition, ctx)
                    ?? ctx;
                (ctx as LocalPluginContext)?.SetCurrentHandler(runtimeHandler.Method.Name);
                TraceHandlerEntry(ctx, runtimeHandler.Method.Name);
                HandlerInvoker.InvokeDelegate(runtimeHandler, [delegateArgument]);
                return;
            }

            // Reflection fallback (no delegate retained). Resolution order matters when a typed request's name
            // collides with a modelled message (e.g. On<MergeRequest>() resolves to "Merge"): that handler's
            // parameter is IMessagePre/PostContext<TRequest>, which the modelled MergePreContext<TEntity> does NOT
            // implement, so the generic message context (built from the handler's own parameter type) is tried
            // first, then the per-message modelled context, then the plain ILocalPluginContext.
            object argument = BuildGenericMessageContext(matchedDefinition.HandlerMethodName!, ctx)
                ?? BuildTypedContext(matchedDefinition, ctx)
                ?? ctx;
            (ctx as LocalPluginContext)?.SetCurrentHandler(matchedDefinition.HandlerMethodName);
            TraceHandlerEntry(ctx, matchedDefinition.HandlerMethodName);
            InvokeTypedHandler(matchedDefinition.HandlerMethodName!, argument);
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

                // Associate/Disassociate are relationship-global messages: the step definition carries TEntity's
                // logical name (stamped by the builder from EntityLogicalNameAttribute), but the runtime context of
                // an (Dis)Associate has no matching single primary entity, so an entity filter here would never
                // match and the step would be dropped. Skip the entity check for these messages. (For sync steps
                // this previously surfaced as "no registered step matching message 'Associate'"; async hid it.)
                bool relationshipMessage = PluginMessages.IsGlobalMessage(ctx.MessageName);

                if (!relationshipMessage &&
                    !string.IsNullOrWhiteSpace(definition.EntityLogicalName) &&
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

        // A generated per-step context (typed step contexts) is a concrete subclass of a Vela step context
        // (PreStepContext<>/PostStepContext<,>) with an (ILocalPluginContext) constructor, exposing typed
        // PreImage/PostImage. Build it directly from the handler's own parameter type so the handler reads the
        // typed projections. Returns null for interfaces and non-context types, so the existing paths take over.
        private static object? BuildDerivedStepContext(Type parameterType, ILocalPluginContext ctx)
        {
            // The derive-check and constructor lookup are the same for a given parameter type on every execution, so
            // resolve them once and cache per type (bounded by the loaded step-context types). Only the ctor.Invoke
            // runs per execution - the sandbox's partial trust rules out compiling a delegate via Reflection.Emit.
            StepContextResolution resolution = StepContextFactory.Resolve(parameterType);
            switch (resolution.Kind)
            {
                case StepContextKind.NotApplicable:
                    // Not a class deriving from a Vela step context: let the existing builders take over.
                    return null;
                case StepContextKind.Abstract:
                    throw new InvalidPluginExecutionException(
                        $"Step handler context '{parameterType.FullName}' is abstract and cannot be constructed; use a concrete generated step context.");
                case StepContextKind.NoConstructor:
                    throw new InvalidPluginExecutionException(
                        $"Step handler context '{parameterType.FullName}' derives from a Vela step context but has no public constructor taking a single ILocalPluginContext, so the pipeline cannot build it.");
                default:
                    // The parameter is (or derives from) a Vela step context, so it is meant to be built here rather
                    // than left to a later builder that would produce an incompatible base context.
                    return resolution.Constructor!.Invoke([ctx]);
            }
        }

        // Messages Vela models with a typed context: build the per-message context so its handler receives a
        // single strongly-typed argument (e.g. IMergeContext<TEntity>). Keyed by the typed trigger's modelled
        // message, not the sdkmessage: OnWinOpportunity() and OnWinQuote() both register on "Win" but take different
        // requests. A permissive OnMessage("Merge") step falls back to its message name. Returns null for a message
        // Vela does not model, so its handler receives the plain ILocalPluginContext.
        private static object? BuildTypedContext(StepDefinition definition, ILocalPluginContext ctx)
        {
            bool post = definition.Stage == PluginStages.PostOperation;
            string message = string.IsNullOrEmpty(definition.ModelledMessage) ? definition.Message : definition.ModelledMessage;
            return message switch
            {
                "Create" => post ? (object)new CreatePostContext<TEntity>(ctx) : new CreatePreContext<TEntity>(ctx),
                "Update" => post ? (object)new UpdatePostContext<TEntity>(ctx) : new UpdatePreContext<TEntity>(ctx),
                "Delete" => post ? (object)new DeletePostContext<TEntity>(ctx) : new DeletePreContext<TEntity>(ctx),
                "CreateMultiple" => post ? (object)new CreateMultiplePostContext<TEntity>(ctx) : new CreateMultiplePreContext<TEntity>(ctx),
                "UpdateMultiple" => post ? (object)new UpdateMultiplePostContext<TEntity>(ctx) : new UpdateMultiplePreContext<TEntity>(ctx),
                "Retrieve" => post ? (object)new RetrievePostContext<TEntity>(ctx) : new RetrievePreContext<TEntity>(ctx),
                "RetrieveMultiple" => post ? (object)new RetrieveMultiplePostContext<TEntity>(ctx) : new RetrieveMultiplePreContext<TEntity>(ctx),
                "Associate" => post ? (object)new AssociatePostContext<TEntity>(ctx) : new AssociatePreContext<TEntity>(ctx),
                "Disassociate" => post ? (object)new DisassociatePostContext<TEntity>(ctx) : new DisassociatePreContext<TEntity>(ctx),
                "Merge" => post ? (object)new MergePostContext<TEntity>(ctx) : new MergePreContext<TEntity>(ctx),
                "GrantAccess" => post ? (object)new GrantAccessPostContext<TEntity>(ctx) : new GrantAccessPreContext<TEntity>(ctx),
                "Assign" => post ? (object)new AssignPostContext<TEntity>(ctx) : new AssignPreContext<TEntity>(ctx),
                "ModifyAccess" => post ? (object)new ModifyAccessPostContext<TEntity>(ctx) : new ModifyAccessPreContext<TEntity>(ctx),
                "RevokeAccess" => post ? (object)new RevokeAccessPostContext<TEntity>(ctx) : new RevokeAccessPreContext<TEntity>(ctx),
                "QualifyLead" => post ? (object)new QualifyLeadPostContext<TEntity>(ctx) : new QualifyLeadPreContext<TEntity>(ctx),
                "WinOpportunity" => post ? (object)new WinOpportunityPostContext<TEntity>(ctx) : new WinOpportunityPreContext<TEntity>(ctx),
                "LoseOpportunity" => post ? (object)new LoseOpportunityPostContext<TEntity>(ctx) : new LoseOpportunityPreContext<TEntity>(ctx),
                "ConvertSalesOrderToInvoice" => post ? (object)new ConvertSalesOrderToInvoicePostContext<TEntity>(ctx) : new ConvertSalesOrderToInvoicePreContext<TEntity>(ctx),
                "GenerateQuoteFromOpportunity" => post ? (object)new GenerateQuoteFromOpportunityPostContext<TEntity>(ctx) : new GenerateQuoteFromOpportunityPreContext<TEntity>(ctx),
                "AddToQueue" => post ? (object)new AddToQueuePostContext<TEntity>(ctx) : new AddToQueuePreContext<TEntity>(ctx),
                "RemoveFromQueue" => post ? (object)new RemoveFromQueuePostContext<TEntity>(ctx) : new RemoveFromQueuePreContext<TEntity>(ctx),
                "PickFromQueue" => post ? (object)new PickFromQueuePostContext<TEntity>(ctx) : new PickFromQueuePreContext<TEntity>(ctx),
                "RouteTo" => post ? (object)new RouteToPostContext<TEntity>(ctx) : new RouteToPreContext<TEntity>(ctx),
                "ReleaseToQueue" => post ? (object)new ReleaseToQueuePostContext<TEntity>(ctx) : new ReleaseToQueuePreContext<TEntity>(ctx),
                "SendEmail" => post ? (object)new SendEmailPostContext<TEntity>(ctx) : new SendEmailPreContext<TEntity>(ctx),
                "SendEmailFromTemplate" => post ? (object)new SendEmailFromTemplatePostContext<TEntity>(ctx) : new SendEmailFromTemplatePreContext<TEntity>(ctx),
                "DeliverIncomingEmail" => post ? (object)new DeliverIncomingEmailPostContext<TEntity>(ctx) : new DeliverIncomingEmailPreContext<TEntity>(ctx),
                "DeliverPromoteEmail" => post ? (object)new DeliverPromoteEmailPostContext<TEntity>(ctx) : new DeliverPromoteEmailPreContext<TEntity>(ctx),
                "AddMembersTeam" => post ? (object)new AddMembersTeamPostContext<TEntity>(ctx) : new AddMembersTeamPreContext<TEntity>(ctx),
                "RemoveMembersTeam" => post ? (object)new RemoveMembersTeamPostContext<TEntity>(ctx) : new RemoveMembersTeamPreContext<TEntity>(ctx),
                "AddListMembersList" => post ? (object)new AddListMembersListPostContext<TEntity>(ctx) : new AddListMembersListPreContext<TEntity>(ctx),
                "AddMemberList" => post ? (object)new AddMemberListPostContext<TEntity>(ctx) : new AddMemberListPreContext<TEntity>(ctx),
                "RemoveMemberList" => post ? (object)new RemoveMemberListPostContext<TEntity>(ctx) : new RemoveMemberListPreContext<TEntity>(ctx),
                "CloseIncident" => post ? (object)new CloseIncidentPostContext<TEntity>(ctx) : new CloseIncidentPreContext<TEntity>(ctx),
                "CancelContract" => post ? (object)new CancelContractPostContext<TEntity>(ctx) : new CancelContractPreContext<TEntity>(ctx),
                "CloneContract" => post ? (object)new CloneContractPostContext<TEntity>(ctx) : new CloneContractPreContext<TEntity>(ctx),
                "RetrievePrincipalAccess" => post ? (object)new RetrievePrincipalAccessPostContext<TEntity>(ctx) : new RetrievePrincipalAccessPreContext<TEntity>(ctx),
                "RetrieveSharedPrincipalsAndAccess" => post ? (object)new RetrieveSharedPrincipalsAndAccessPostContext<TEntity>(ctx) : new RetrieveSharedPrincipalsAndAccessPreContext<TEntity>(ctx),
                "WinQuote" => post ? (object)new WinQuotePostContext<TEntity>(ctx) : new WinQuotePreContext<TEntity>(ctx),
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

                object? context = BuildGenericMessageContextForType(parameters[0].ParameterType, ctx);
                if (context != null)
                {
                    return context;
                }
            }

            return null;
        }

        // Builds the concrete generic message context (MessagePreContext / MessagePostContext, one- or two-arg)
        // that matches a handler parameter typed as IMessagePre/PostContext<TRequest>[,TEntity], over the live
        // pipeline context. Returns null for any other parameter type (a modelled context, or the plain
        // ILocalPluginContext), so the caller falls back to the per-message modelled context. This is the single
        // source of truth used both by the name-based reflection path and the retained-delegate path, so guided
        // and typed steps resolve their context through one rule.
        private static object? BuildGenericMessageContextForType(Type parameterType, ILocalPluginContext ctx)
        {
            if (!parameterType.IsGenericType)
            {
                return null;
            }

            // Disambiguate by the parameter's own generic type definition. A two-arg entity-typed context
            // (IMessagePreContext<TRequest, TEntity>) is matched directly because the parameter type is that
            // two-arg interface - even though it also implements the one-arg IMessagePreContext<TRequest>, the
            // parameter's GetGenericTypeDefinition() reports the two-arg form.
            Type definition = parameterType.GetGenericTypeDefinition();
            Type contextDefinition;
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
                return null;
            }

            Type contextType = contextDefinition.MakeGenericType(parameterType.GetGenericArguments());
            return Activator.CreateInstance(contextType, ctx);
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
