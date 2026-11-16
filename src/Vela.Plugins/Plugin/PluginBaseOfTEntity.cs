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
    /// The pre- and post-stage typed context of one modelled message: their types (checked against a handler's
    /// parameter when the steps are registered) and how to build each over the live pipeline context (no reflection
    /// per execution).
    /// </summary>
    internal sealed class TypedContextFactory
    {
        private readonly Func<ILocalPluginContext, object> _pre;
        private readonly Func<ILocalPluginContext, object> _post;

        private TypedContextFactory(Type preType, Func<ILocalPluginContext, object> pre, Type postType, Func<ILocalPluginContext, object> post)
        {
            PreType = preType;
            PostType = postType;
            _pre = pre;
            _post = post;
        }

        /// <summary>The context built at PreValidation / PreOperation.</summary>
        public Type PreType { get; }

        /// <summary>The context built at PostOperation.</summary>
        public Type PostType { get; }

        public static TypedContextFactory Of<TPre, TPost>(Func<ILocalPluginContext, TPre> pre, Func<ILocalPluginContext, TPost> post)
            where TPre : class
            where TPost : class
            => new TypedContextFactory(typeof(TPre), pre, typeof(TPost), post);

        public object Build(bool post, ILocalPluginContext ctx) => post ? _post(ctx) : _pre(ctx);
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
                        EnsureHandlerAcceptsContext(built, step.RuntimeHandler);
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
                }

                // Several steps may share a trigger (message + stage + mode + entity) when their filters, images,
                // relationship scope or step keys tell them apart; two that resolve to the same step key cannot be
                // deployed or dispatched separately, so fail fast.
                try
                {
                    StepIdentity.AssignStepKeys(definitions, pluginTypeName);
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidPluginExecutionException(ex.Message, ex);
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
            StepDefinition? matchedDefinition = ResolveSingleMatchingStep(ctx, definitions, out bool outOfRelationshipScope);

            if (matchedDefinition == null)
            {
                if (outOfRelationshipScope)
                {
                    // An Associate/Disassociate step fires for every relationship in Dataverse; .ForRelationship(...)
                    // scopes it, so another relationship is not an error, just not this step's business.
                    SkipHandler(ctx, "Vela: relationship outside the step's ForRelationship scope; nothing to run.");
                    return;
                }

                throw new InvalidPluginExecutionException(
                    $"Plugin '{GetType().FullName}' has no registered step matching message '{ctx.MessageName}', " +
                    $"stage {DescribeEnum<PluginStages>(ctx.Stage)}, mode {DescribeEnum<ExecutionMode>(ctx.Mode)} on entity '{ctx.PrimaryEntityName}'.");
            }

            // A step-level depth limit (.SkipWhenDepthAbove(n)) replaces the plugin-level guard: past it, the handler is
            // skipped rather than the execution failing.
            if (matchedDefinition.SkipWhenDepthAbove is int stepMaxDepth && ctx.Depth > stepMaxDepth)
            {
                SkipHandler(ctx, $"Vela: step skipped by SkipWhenDepthAbove. Depth={ctx.Depth}, MaxDepth={stepMaxDepth}");
                return;
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
                    ?? BuildTypedContext(TypedMessageOf(matchedDefinition), matchedDefinition.Stage, ctx)
                    ?? ctx;
                EnterHandler(ctx, runtimeHandler.Method.Name);
                HandlerInvoker.InvokeDelegate(runtimeHandler, [delegateArgument]);
                return;
            }

            // Reflection fallback (no delegate retained). Resolution order matters when a typed request's name
            // collides with a modelled message (e.g. On<MergeRequest>() resolves to "Merge"): that handler's
            // parameter is IMessagePre/PostContext<TRequest>, which the modelled MergePreContext<TEntity> does NOT
            // implement, so the generic message context (built from the handler's own parameter type) is tried
            // first, then the per-message modelled context, then the plain ILocalPluginContext.
            object argument = BuildGenericMessageContext(matchedDefinition.HandlerMethodName!, ctx)
                ?? BuildTypedContext(TypedMessageOf(matchedDefinition), matchedDefinition.Stage, ctx)
                ?? ctx;
            EnterHandler(ctx, matchedDefinition.HandlerMethodName);
            InvokeTypedHandler(matchedDefinition.HandlerMethodName!, argument);
        }

        // The enum member's name when the platform value maps to one (PreOperation, Synchronous), else the raw value.
        private static string DescribeEnum<TEnum>(int value)
            where TEnum : struct
            => Enum.IsDefined(typeof(TEnum), value) ? Enum.GetName(typeof(TEnum), value)! : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

        private protected override bool DispatchesToHandler => true;

        private protected override bool HasStepDepthLimit(ILocalPluginContext ctx)
        {
            try
            {
                return ResolveSingleMatchingStep(ctx, GetStepDefinitions(), out _)?.SkipWhenDepthAbove != null;
            }
            catch (InvalidPluginExecutionException)
            {
                // No single step resolves: the dispatch reports it; the plugin-level guard applies meanwhile.
                return false;
            }
        }

        private StepDefinition? ResolveSingleMatchingStep(ILocalPluginContext ctx, StepDefinition[] definitions, out bool outOfRelationshipScope)
        {
            outOfRelationshipScope = false;
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

                // Associate/Disassociate (and the other PluginMessages.GlobalMessages) are global messages: the step
                // definition may carry TEntity's logical name (stamped by the builder from EntityLogicalNameAttribute),
                // but their runtime context has no matching single primary entity, so an entity filter here would never
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

            // Drop relationship-scoped steps for another relationship; when several steps share the trigger, the
            // executing step (OwningExtension) picks its own.
            List<StepDefinition> selected = StepIdentity.Select(matches, ctx, GetType().FullName ?? GetType().Name);
            if (selected.Count == 0)
            {
                outOfRelationshipScope = true;
                return null;
            }

            return selected[0];
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

        // The permissive Handle(Delegate) overload compiles any one-parameter method, so a handler typed for another
        // message or stage (an OnAssign() step bound to an IUpdatePostContext<T> handler) would otherwise fail on
        // every execution with a raw cast error. Check once, when the steps are registered, that the context the
        // dispatch builds for this step fits the handler's parameter, and name the one it expects.
        private void EnsureHandlerAcceptsContext(StepDefinition definition, Delegate handler)
        {
            Type parameterType = handler.Method.GetParameters()[0].ParameterType;
            string message = TypedMessageOf(definition);
            bool post = definition.Stage == PluginStages.PostOperation;
            Type? modelled = ExpectedContextType(message, definition.Stage);
            string? problem = HandlerContextProblem(parameterType, definition, post, modelled);
            if (problem == null)
            {
                return;
            }

            string expected = modelled != null
                ? $"{ExpressionHelper.DisplayName(ContextInterfaceOf(modelled))} (or its generated context, or ILocalPluginContext)"
                : $"ILocalPluginContext, or IMessage{(post ? "Post" : "Pre")}Context<TRequest> for the {message} request";
            throw new InvalidPluginExecutionException(
                $"Plugin '{GetType().FullName}': handler '{handler.Method.Name}' is bound to the {message} {definition.Stage} step " +
                $"but takes '{ExpressionHelper.DisplayName(parameterType)}'; {problem}. This step passes {expected}.");
        }

        // Why the context the dispatch builds for this step does not fit the handler's parameter, or null when it does.
        // Mirrors the dispatch order: a derived (generated) step context, then a generic message context, then the
        // modelled context or the plain ILocalPluginContext.
        private static string? HandlerContextProblem(Type parameterType, StepDefinition definition, bool post, Type? modelled)
        {
            Type? requestType;
            bool postContext;
            if (StepContextFactory.Resolve(parameterType).Kind != StepContextKind.NotApplicable)
            {
                if (modelled != null)
                {
                    return modelled.IsAssignableFrom(parameterType) ? null : "it is a context for another message or stage";
                }

                if (!TryGetGenericMessageContext(parameterType, isInterface: false, out requestType, out postContext))
                {
                    return "it is a context for a modelled message, not this one";
                }
            }
            else if (TryGetGenericMessageContext(parameterType, isInterface: true, out requestType, out postContext))
            {
                if (modelled != null)
                {
                    return "a generic message context is for a message without a typed trigger";
                }
            }
            else
            {
                Type built = modelled ?? typeof(ILocalPluginContext);
                return parameterType.IsAssignableFrom(built) ? null : "it is a context for another message or stage";
            }

            if (postContext && !post)
            {
                return "a post-operation context has no response before the main operation";
            }

            // OnMessage<TMessage>().Handle<TRequest>(...) states the message twice: the request read must be the
            // message's own, or ctx.Request reads the parameters of another message.
            string? requestMessage = ExpressionHelper.TryGetMessageName(requestType!);
            if (requestMessage != null
                && !string.Equals(requestMessage, definition.RequestName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requestMessage, definition.Message, StringComparison.OrdinalIgnoreCase))
            {
                return $"its request {ExpressionHelper.DisplayName(requestType!)} is the {requestMessage} message's, not this step's";
            }

            return null;
        }

        // Whether the type is a generic message context (IMessagePre/PostContext<TRequest[, TEntity]> when isInterface,
        // else a class deriving from MessagePre/PostContext<TRequest[, TEntity]>), with its request type and stage side.
        private static bool TryGetGenericMessageContext(Type type, bool isInterface, out Type? requestType, out bool post)
        {
            requestType = null;
            post = false;
            for (Type? t = type; t != null && t != typeof(object); t = isInterface ? null : t.BaseType)
            {
                if (!t.IsGenericType)
                {
                    continue;
                }

                Type definition = t.GetGenericTypeDefinition();
                bool pre = isInterface
                    ? definition == typeof(IMessagePreContext<>) || definition == typeof(IMessagePreContext<,>)
                    : definition == typeof(MessagePreContext<>) || definition == typeof(MessagePreContext<,>);
                post = isInterface
                    ? definition == typeof(IMessagePostContext<>) || definition == typeof(IMessagePostContext<,>)
                    : definition == typeof(MessagePostContext<>) || definition == typeof(MessagePostContext<,>);
                if (pre || post)
                {
                    requestType = t.GetGenericArguments()[0];
                    return true;
                }
            }

            post = false;
            return false;
        }

        // The I{Message}{Pre|Post}Context<TEntity> interface a modelled context implements: what a handler declares.
        private static Type ContextInterfaceOf(Type context)
            => context.GetInterfaces().FirstOrDefault(i => i.Name == "I" + context.Name) ?? context;

        // The request name the step was declared with picks its typed context: one sdkmessage can serve several
        // requests (Win is WinOpportunity on opportunity and WinQuote on quote).
        private static string TypedMessageOf(StepDefinition definition)
            => string.IsNullOrEmpty(definition.RequestName) ? definition.Message : definition.RequestName;

        // Messages Vela models with a typed context: the per-message context built for each stage, so its handler
        // receives a single strongly-typed argument (e.g. IMergePostContext<TEntity>). One table serves the dispatch
        // (BuildTypedContext) and the registration check that the handler accepts that context (ExpectedContextType).
        // Keyed by the request name the step was declared with (ordinal, as the step builder records it).
        private static readonly Dictionary<string, TypedContextFactory> TypedContexts =
            new Dictionary<string, TypedContextFactory>(StringComparer.Ordinal)
            {
                [PluginMessages.Create] = TypedContextFactory.Of(c => new CreatePreContext<TEntity>(c), c => new CreatePostContext<TEntity>(c)),
                [PluginMessages.Update] = TypedContextFactory.Of(c => new UpdatePreContext<TEntity>(c), c => new UpdatePostContext<TEntity>(c)),
                [PluginMessages.Delete] = TypedContextFactory.Of(c => new DeletePreContext<TEntity>(c), c => new DeletePostContext<TEntity>(c)),
                [PluginMessages.CreateMultiple] = TypedContextFactory.Of(c => new CreateMultiplePreContext<TEntity>(c), c => new CreateMultiplePostContext<TEntity>(c)),
                [PluginMessages.UpdateMultiple] = TypedContextFactory.Of(c => new UpdateMultiplePreContext<TEntity>(c), c => new UpdateMultiplePostContext<TEntity>(c)),
                [PluginMessages.Retrieve] = TypedContextFactory.Of(c => new RetrievePreContext<TEntity>(c), c => new RetrievePostContext<TEntity>(c)),
                [PluginMessages.RetrieveMultiple] = TypedContextFactory.Of(c => new RetrieveMultiplePreContext<TEntity>(c), c => new RetrieveMultiplePostContext<TEntity>(c)),
                [PluginMessages.Associate] = TypedContextFactory.Of(c => new AssociatePreContext<TEntity>(c), c => new AssociatePostContext<TEntity>(c)),
                [PluginMessages.Disassociate] = TypedContextFactory.Of(c => new DisassociatePreContext<TEntity>(c), c => new DisassociatePostContext<TEntity>(c)),
                [PluginMessages.Merge] = TypedContextFactory.Of(c => new MergePreContext<TEntity>(c), c => new MergePostContext<TEntity>(c)),
                [PluginMessages.GrantAccess] = TypedContextFactory.Of(c => new GrantAccessPreContext<TEntity>(c), c => new GrantAccessPostContext<TEntity>(c)),
                [PluginMessages.Assign] = TypedContextFactory.Of(c => new AssignPreContext<TEntity>(c), c => new AssignPostContext<TEntity>(c)),
                [PluginMessages.ModifyAccess] = TypedContextFactory.Of(c => new ModifyAccessPreContext<TEntity>(c), c => new ModifyAccessPostContext<TEntity>(c)),
                [PluginMessages.RevokeAccess] = TypedContextFactory.Of(c => new RevokeAccessPreContext<TEntity>(c), c => new RevokeAccessPostContext<TEntity>(c)),
                [PluginMessages.QualifyLead] = TypedContextFactory.Of(c => new QualifyLeadPreContext<TEntity>(c), c => new QualifyLeadPostContext<TEntity>(c)),
                [PluginMessages.WinOpportunity] = TypedContextFactory.Of(c => new WinOpportunityPreContext<TEntity>(c), c => new WinOpportunityPostContext<TEntity>(c)),
                [PluginMessages.LoseOpportunity] = TypedContextFactory.Of(c => new LoseOpportunityPreContext<TEntity>(c), c => new LoseOpportunityPostContext<TEntity>(c)),
                [PluginMessages.ConvertSalesOrderToInvoice] = TypedContextFactory.Of(c => new ConvertSalesOrderToInvoicePreContext<TEntity>(c), c => new ConvertSalesOrderToInvoicePostContext<TEntity>(c)),
                [PluginMessages.GenerateQuoteFromOpportunity] = TypedContextFactory.Of(c => new GenerateQuoteFromOpportunityPreContext<TEntity>(c), c => new GenerateQuoteFromOpportunityPostContext<TEntity>(c)),
                [PluginMessages.AddToQueue] = TypedContextFactory.Of(c => new AddToQueuePreContext<TEntity>(c), c => new AddToQueuePostContext<TEntity>(c)),
                [PluginMessages.RemoveFromQueue] = TypedContextFactory.Of(c => new RemoveFromQueuePreContext<TEntity>(c), c => new RemoveFromQueuePostContext<TEntity>(c)),
                [PluginMessages.PickFromQueue] = TypedContextFactory.Of(c => new PickFromQueuePreContext<TEntity>(c), c => new PickFromQueuePostContext<TEntity>(c)),
                [PluginMessages.RouteTo] = TypedContextFactory.Of(c => new RouteToPreContext<TEntity>(c), c => new RouteToPostContext<TEntity>(c)),
                [PluginMessages.ReleaseToQueue] = TypedContextFactory.Of(c => new ReleaseToQueuePreContext<TEntity>(c), c => new ReleaseToQueuePostContext<TEntity>(c)),
                [PluginMessages.SendEmail] = TypedContextFactory.Of(c => new SendEmailPreContext<TEntity>(c), c => new SendEmailPostContext<TEntity>(c)),
                [PluginMessages.SendEmailFromTemplate] = TypedContextFactory.Of(c => new SendEmailFromTemplatePreContext<TEntity>(c), c => new SendEmailFromTemplatePostContext<TEntity>(c)),
                [PluginMessages.DeliverIncomingEmail] = TypedContextFactory.Of(c => new DeliverIncomingEmailPreContext<TEntity>(c), c => new DeliverIncomingEmailPostContext<TEntity>(c)),
                [PluginMessages.DeliverPromoteEmail] = TypedContextFactory.Of(c => new DeliverPromoteEmailPreContext<TEntity>(c), c => new DeliverPromoteEmailPostContext<TEntity>(c)),
                [PluginMessages.AddMembersTeam] = TypedContextFactory.Of(c => new AddMembersTeamPreContext<TEntity>(c), c => new AddMembersTeamPostContext<TEntity>(c)),
                [PluginMessages.RemoveMembersTeam] = TypedContextFactory.Of(c => new RemoveMembersTeamPreContext<TEntity>(c), c => new RemoveMembersTeamPostContext<TEntity>(c)),
                [PluginMessages.AddListMembersList] = TypedContextFactory.Of(c => new AddListMembersListPreContext<TEntity>(c), c => new AddListMembersListPostContext<TEntity>(c)),
                [PluginMessages.AddMemberList] = TypedContextFactory.Of(c => new AddMemberListPreContext<TEntity>(c), c => new AddMemberListPostContext<TEntity>(c)),
                [PluginMessages.RemoveMemberList] = TypedContextFactory.Of(c => new RemoveMemberListPreContext<TEntity>(c), c => new RemoveMemberListPostContext<TEntity>(c)),
                [PluginMessages.CloseIncident] = TypedContextFactory.Of(c => new CloseIncidentPreContext<TEntity>(c), c => new CloseIncidentPostContext<TEntity>(c)),
                [PluginMessages.CancelContract] = TypedContextFactory.Of(c => new CancelContractPreContext<TEntity>(c), c => new CancelContractPostContext<TEntity>(c)),
                [PluginMessages.CloneContract] = TypedContextFactory.Of(c => new CloneContractPreContext<TEntity>(c), c => new CloneContractPostContext<TEntity>(c)),
                [PluginMessages.RetrievePrincipalAccess] = TypedContextFactory.Of(c => new RetrievePrincipalAccessPreContext<TEntity>(c), c => new RetrievePrincipalAccessPostContext<TEntity>(c)),
                [PluginMessages.RetrieveSharedPrincipalsAndAccess] = TypedContextFactory.Of(c => new RetrieveSharedPrincipalsAndAccessPreContext<TEntity>(c), c => new RetrieveSharedPrincipalsAndAccessPostContext<TEntity>(c)),
                [PluginMessages.WinQuote] = TypedContextFactory.Of(c => new WinQuotePreContext<TEntity>(c), c => new WinQuotePostContext<TEntity>(c)),
                [PluginMessages.AddItemCampaign] = TypedContextFactory.Of(c => new AddItemCampaignPreContext<TEntity>(c), c => new AddItemCampaignPostContext<TEntity>(c)),
                [PluginMessages.RemoveItemCampaign] = TypedContextFactory.Of(c => new RemoveItemCampaignPreContext<TEntity>(c), c => new RemoveItemCampaignPostContext<TEntity>(c)),
                [PluginMessages.Recalculate] = TypedContextFactory.Of(c => new RecalculatePreContext<TEntity>(c), c => new RecalculatePostContext<TEntity>(c)),
                [PluginMessages.LockSalesOrderPricing] = TypedContextFactory.Of(c => new LockSalesOrderPricingPreContext<TEntity>(c), c => new LockSalesOrderPricingPostContext<TEntity>(c)),
                [PluginMessages.UnlockSalesOrderPricing] = TypedContextFactory.Of(c => new UnlockSalesOrderPricingPreContext<TEntity>(c), c => new UnlockSalesOrderPricingPostContext<TEntity>(c)),
                [PluginMessages.LockInvoicePricing] = TypedContextFactory.Of(c => new LockInvoicePricingPreContext<TEntity>(c), c => new LockInvoicePricingPostContext<TEntity>(c)),
                [PluginMessages.UnlockInvoicePricing] = TypedContextFactory.Of(c => new UnlockInvoicePricingPreContext<TEntity>(c), c => new UnlockInvoicePricingPostContext<TEntity>(c)),
                [PluginMessages.Reschedule] = TypedContextFactory.Of(c => new ReschedulePreContext<TEntity>(c), c => new ReschedulePostContext<TEntity>(c)),
                [PluginMessages.ImmediateBook] = TypedContextFactory.Of(c => new ImmediateBookPreContext<TEntity>(c), c => new ImmediateBookPostContext<TEntity>(c)),
                [PluginMessages.SendTemplate] = TypedContextFactory.Of(c => new SendTemplatePreContext<TEntity>(c), c => new SendTemplatePostContext<TEntity>(c)),
                [PluginMessages.SendFax] = TypedContextFactory.Of(c => new SendFaxPreContext<TEntity>(c), c => new SendFaxPostContext<TEntity>(c)),
                [PluginMessages.AddProductToKit] = TypedContextFactory.Of(c => new AddProductToKitPreContext<TEntity>(c), c => new AddProductToKitPostContext<TEntity>(c)),
                [PluginMessages.RemoveProductFromKit] = TypedContextFactory.Of(c => new RemoveProductFromKitPreContext<TEntity>(c), c => new RemoveProductFromKitPostContext<TEntity>(c)),
            };

        // Returns null for a permissive OnMessage<TMessage>() / On<TRequest>() step that Vela does not model, so its
        // handler receives the plain ILocalPluginContext or a generic message context.
        private static object? BuildTypedContext(string? message, PluginStages stage, ILocalPluginContext ctx)
            => message != null && TypedContexts.TryGetValue(message, out TypedContextFactory? factory)
                ? factory.Build(stage == PluginStages.PostOperation, ctx)
                : null;

        // The concrete context the dispatch builds for a modelled message at a stage, or null when it builds none.
        private static Type? ExpectedContextType(string? message, PluginStages stage)
            => message != null && TypedContexts.TryGetValue(message, out TypedContextFactory? factory)
                ? (stage == PluginStages.PostOperation ? factory.PostType : factory.PreType)
                : null;

        // A permissive OnMessage<TMessage>() step whose handler takes a generic message context
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
