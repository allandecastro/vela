using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.CustomApi;
using Vela.Manifest;
using Vela.Steps;
using Vela.Testing.Security;

namespace Vela.Testing
{
    // The execution frame: depth, shared variables, the rollback scopes and transactions around an operation, the
    // queued asynchronous steps, the identity a call runs as, and the plugin execution itself.
    public sealed partial class PluginPipeline
    {
        private T RunOperation<T>(Func<T> body, ParameterCollection? sharedVariables = null, bool transactional = true)
        {
            _depth++;
            ParameterCollection previousShared = _sharedVariables;
            bool previousEnclosed = _operationEnclosedInTransaction;
            // Top-level operation starts from the supplied shared variables (e.g. the request 'tag'); a cascaded
            // write inherits the parent operation's collection so upstream shared variables remain visible.
            _sharedVariables = _depth == 1 ? (sharedVariables ?? new ParameterCollection()) : previousShared;
            try
            {
                if (_depth > _maxDepth)
                {
                    throw new InvalidPluginExecutionException(
                        $"Vela pipeline stopped at depth {_depth} (cap {_maxDepth}). Likely infinite recursion between plugins.");
                }

                _operationEnclosedInTransaction = _openTransactions > 0;
                return RunAtomically(body, transactional);
            }
            finally
            {
                _operationEnclosedInTransaction = previousEnclosed;
                _sharedVariables = previousShared;
                _depth--;
            }
        }

        // Runs the body all-or-nothing: if it throws, the store is restored and the async steps queued inside it are
        // dropped. The async steps run only once the outermost scope has committed.
        private T RunAtomically<T>(Func<T> body, bool transactional)
        {
            bool outermost = _openScopes == 0;
            if (outermost)
            {
                _correlationId = Guid.NewGuid();
            }

            var snapshot = _store.Snapshot();
            int queued = _pendingAsyncSteps.Count;
            _openScopes++;
            if (transactional)
            {
                _openTransactions++;
            }

            T result;
            try
            {
                result = body();
            }
            catch
            {
                _store.RestoreTo(snapshot);
                _pendingAsyncSteps.RemoveRange(queued, _pendingAsyncSteps.Count - queued);
                throw;
            }
            finally
            {
                _openScopes--;
                if (transactional)
                {
                    _openTransactions--;
                }
            }

            if (outermost)
            {
                RunPendingAsyncSteps();
            }

            return result;
        }

        // Queues an async step with the state it was triggered in, to run once the outermost scope commits.
        private void QueueAsyncStep(Action dispatch)
        {
            _pendingAsyncSteps.Add(new PendingAsyncStep
            {
                Dispatch = dispatch,
                Depth = _depth,
                SharedVariables = _sharedVariables,
                Bypass = _bypass,
                CallerUserId = _callerUserId,
                InitiatingUserId = _initiatingUserId,
                CorrelationId = _correlationId,
                RequestId = _requestId,
                ParentContext = _contextStack.Count > 0 ? _contextStack.Peek() : null,
            });
        }

        // Runs the queued async steps in order. A step's own writes may queue more, which run after it. Real Dataverse
        // runs async steps as decoupled system jobs whose failures never surface to the triggering caller, so any
        // exception is recorded (assert via AsyncStepFailures) instead of thrown out of the originating operation.
        private void RunPendingAsyncSteps()
        {
            while (_pendingAsyncSteps.Count > 0)
            {
                PendingAsyncStep step = _pendingAsyncSteps[0];
                _pendingAsyncSteps.RemoveAt(0);
                RunAsyncStep(step);
            }
        }

        // Runs one async step as its own system job: outside any transaction, in its own rollback scope (a failure
        // undoes its writes and drops the async steps they queued), and with the state captured when it was queued.
        private void RunAsyncStep(PendingAsyncStep step)
        {
            int previousDepth = _depth;
            ParameterCollection previousShared = _sharedVariables;
            BypassContext previousBypass = _bypass;
            Guid? previousCaller = _callerUserId;
            Guid? previousInitiating = _initiatingUserId;
            Guid previousCorrelation = _correlationId;
            Guid previousRequest = _requestId;

            _depth = step.Depth;
            _sharedVariables = step.SharedVariables;
            _bypass = step.Bypass;
            _callerUserId = step.CallerUserId;
            _initiatingUserId = step.InitiatingUserId;
            _correlationId = step.CorrelationId;
            _requestId = step.RequestId;
            _asyncOperationId = Guid.NewGuid();
            if (step.ParentContext != null)
            {
                _contextStack.Push(step.ParentContext);
            }

            var snapshot = _store.Snapshot();
            int queued = _pendingAsyncSteps.Count;
            _openScopes++;
            try
            {
                if (step.CallerUserId is Guid caller)
                {
                    _store.RunAs(caller == SystemUserId ? (Guid?)null : caller, () =>
                    {
                        step.Dispatch();
                        return true;
                    });
                }
                else
                {
                    step.Dispatch();
                }
            }
            catch (Exception ex)
            {
                _store.RestoreTo(snapshot);
                _pendingAsyncSteps.RemoveRange(queued, _pendingAsyncSteps.Count - queued);
                _asyncStepFailures.Add(ex);
            }
            finally
            {
                _openScopes--;
                if (step.ParentContext != null)
                {
                    _contextStack.Pop();
                }

                _depth = previousDepth;
                _sharedVariables = previousShared;
                _bypass = previousBypass;
                _callerUserId = previousCaller;
                _initiatingUserId = previousInitiating;
                _correlationId = previousCorrelation;
                _requestId = previousRequest;
            }
        }

        // Live, an async step runs outside any transaction. A PreValidation step runs before its operation's
        // transaction begins, so it is in one only when its operation is nested in another's transaction (verified
        // live: false at depth 1, true nested). Every other stage runs in the operation's transaction, which the
        // non-transactional reads do not open.
        private bool IsInTransactionFor(PluginStages stage, ExecutionMode mode)
        {
            if (mode == ExecutionMode.Asynchronous)
            {
                return false;
            }

            return stage == PluginStages.PreValidation ? _operationEnclosedInTransaction : _openTransactions > 0;
        }

        // Runs a call as the identity of the service that issued it (see PipelineOrganizationService): the store
        // enforces security as that user (or not at all for SYSTEM), and plugins fired by the call see that user as
        // ctx.UserId. The test-facing service (Caller) keeps whatever identity is in force. The outermost caller is
        // remembered as the initiating user, which never changes through impersonation or SYSTEM calls. A call that
        // enters the pipeline from outside (no request in progress) starts a new request id.
        internal T RunAs<T>(PipelineIdentity identity, Func<T> body)
        {
            if (_inRequest)
            {
                return RunAsCore(identity, body);
            }

            _inRequest = true;
            _requestId = Guid.NewGuid();
            try
            {
                return RunAsCore(identity, body);
            }
            finally
            {
                _inRequest = false;
            }
        }

        private T RunAsCore<T>(PipelineIdentity identity, Func<T> body)
        {
            if (identity.IsCaller)
            {
                return body();
            }

            bool system = identity.IsSystem || identity.UserId == SystemUserId;
            Guid? previousCaller = _callerUserId;
            Guid? previousInitiating = _initiatingUserId;
            _initiatingUserId ??= _callerUserId ?? _store.CurrentActor().UserId;
            _callerUserId = system ? SystemUserId : identity.UserId;
            try
            {
                return _store.RunAs(system ? (Guid?)null : identity.UserId, body);
            }
            finally
            {
                _callerUserId = previousCaller;
                _initiatingUserId = previousInitiating;
            }
        }

        // Finalizes a freshly built context and runs the plugin. Stamps the calling identity (so ctx.UserId/
        // InitiatingUserId/BusinessUnitId/OrganizationId match WhoAmI and system-field stamping instead of being
        // random): UserId is the step's impersonated user (WithImpersonation(Guid)) when set, otherwise the identity
        // of the service that issued the triggering call; InitiatingUserId is always the outermost caller. Links the
        // ParentContext for a cascaded (nested) execution, and keeps the context on the stack while the plugin runs
        // so a write it triggers sees it as the parent.
        private void ExecuteWithContext(FakePluginExecutionContext context, IPlugin plugin, StepDefinition? step = null)
        {
            (Guid actingUserId, Guid businessUnitId, Guid organizationId) = _store.CurrentActor();
            Guid caller = _callerUserId ?? actingUserId;
            Guid userId = step?.ImpersonatingUserId is Guid impersonated && impersonated != Guid.Empty ? impersonated : caller;
            if (userId != caller && userId != SystemUserId)
            {
                // The impersonated user's own business unit (from its security registration, when configured).
                businessUnitId = _store.RunAs<Guid>(userId, () => _store.CurrentActor().BusinessUnitId);
            }

            context.UserId = userId;
            context.InitiatingUserId = _initiatingUserId ?? actingUserId;
            context.BusinessUnitId = businessUnitId;
            context.OrganizationId = organizationId;
            context.ParentContext = _contextStack.Count > 0 ? _contextStack.Peek() : null;

            // Verified live: every step reports the sandbox isolation mode (2) and "none" as SecondaryEntityName.
            context.RequestId = _requestId;
            context.OperationId = context.Mode == (int)ExecutionMode.Asynchronous ? _asyncOperationId : _requestId;
            context.IsolationMode = 2;
            context.SecondaryEntityName = "none";

            _contextStack.Push(context);
            try
            {
                plugin.Execute(BuildProvider(context));
            }
            finally
            {
                _contextStack.Pop();
            }
        }

        private IServiceProvider BuildProvider(FakePluginExecutionContext context)
        {
            // ctx.Service is CreateOrganizationService(ctx.UserId): a service acting as that user (subject to the
            // security model). ctx.SystemService is CreateOrganizationService(null): SYSTEM, not subject to it.
            IOrganizationServiceFactory factory = new PipelineServiceFactory(this);

            Dictionary<Type, object> services = new Dictionary<Type, object>
            {
                [typeof(IPluginExecutionContext)] = context,
                [typeof(IOrganizationServiceFactory)] = factory,
                [typeof(ITracingService)] = _trace,
                [typeof(Microsoft.Xrm.Sdk.PluginTelemetry.ILogger)] = _logger
            };

            return new DictionaryServiceProvider(services);
        }

        // Hands plugins identity-bound pipeline services: null (or SystemUserId) -> SYSTEM, any other id -> that user.
        private sealed class PipelineServiceFactory : IOrganizationServiceFactory
        {
            private readonly PluginPipeline _pipeline;

            public PipelineServiceFactory(PluginPipeline pipeline)
            {
                _pipeline = pipeline;
            }

            public IOrganizationService CreateOrganizationService(Guid? userId)
            {
                return userId == null || userId.Value == SystemUserId
                    ? new PipelineOrganizationService(_pipeline, PipelineIdentity.System)
                    : new PipelineOrganizationService(_pipeline, PipelineIdentity.User(userId.Value));
            }
        }

        // An async step waiting for the outermost scope to commit, with the state it was triggered in.
        private sealed class PendingAsyncStep
        {
            public Action Dispatch = null!;
            public int Depth;
            public ParameterCollection SharedVariables = null!;
            public BypassContext Bypass = null!;
            public Guid? CallerUserId;
            public Guid? InitiatingUserId;
            public Guid CorrelationId;
            public Guid RequestId;
            public FakePluginExecutionContext? ParentContext;
        }
    }
}
