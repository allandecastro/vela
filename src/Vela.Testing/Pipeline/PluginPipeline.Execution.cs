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
    // The execution frame: depth, shared variables, the snapshot/rollback around an operation, the identity a call
    // runs as, and the plugin execution itself (context stamping, ParentContext, services).
    public sealed partial class PluginPipeline
    {
        // ---- Transactions --------------------------------------------------------------------------------------
        //
        // The database transaction the synchronous pipeline is running in, mirroring Dataverse's stage model:
        // - The PreValidation stage of the initial operation runs BEFORE the transaction (IsInTransaction = false), so a
        //   write a PreValidation plugin makes is its own operation and commits on its own.
        // - The transaction opens before PreOperation; an operation triggered from inside it (a plugin's write, at any
        //   stage, including its PreValidation) joins it rather than opening its own.
        // - Any synchronous failure inside the transaction rolls the whole transaction back. A plugin that catches the
        //   failure of one of its own writes cannot carry on: the transaction has ended, so its next service call
        //   fails ("There is no active transaction") and returning normally fails ("ISV code reduced the open
        //   transaction count"), as on the platform. A caught failed read, or a write denied by its Read check, does
        //   not end it (verified live).
        // - Asynchronous steps are queued in the transaction and run only after it commits (outside any transaction,
        //   once the outermost service call returns); a rolled-back transaction discards them.

        private PipelineTransaction? _transaction;

        // The operations on the (synchronous) call stack; the one that opened the transaction owns its commit/rollback.
        private readonly Stack<OperationFrame> _frames = new Stack<OperationFrame>();

        // Set while the correlation id in force must not change: inside an ExecuteTransaction batch (its children share
        // one id) and while an asynchronous step runs (its writes keep the id of the operation that queued it).
        private bool _correlationPinned;

        // Asynchronous steps of committed transactions, run when the outermost service call returns.
        private readonly Queue<Action> _committedAsync = new Queue<Action>();
        private int _callDepth;
        private bool _drainingAsync;

        // The platform fault codes for a plugin that swallows a failed service call inside a transaction (see
        // "Transaction errors" in the Dataverse plug-in troubleshooting docs).
        internal const int IsvReducedTransactionCount = -2146893812;
        internal const int NoActiveTransaction = -2147220911;

        // Runs one operation. A top-level operation starts from the supplied shared variables (e.g. the request 'tag').
        // An operation a plugin issues starts from its own collection too: the calling plugin's variables reach it only
        // through ParentContext, and nothing it sets flows back (verified live). A platform-nested operation with no
        // plugin on the stack (a deep insert's child) keeps the enclosing operation's collection.
        // innerOf: the operation is the main operation of another message at the same depth (the Create or Update of an
        // Upsert, verified live): its contexts have that message's stage-30 context as ParentContext.
        private T RunOperation<T>(Func<List<Action>, T> body, ParameterCollection? sharedVariables = null, string? bulkMessage = null, FakePluginExecutionContext? innerOf = null)
        {
            bool inner = innerOf != null;
            OperationFrame? outer = inner ? _frames.Peek() : null;
            if (!inner)
            {
                _depth++;
            }

            ParameterCollection previousShared = _sharedVariables;
            _sharedVariables = inner
                ? CopyParameters(outer!.InitialShared)
                : _depth == 1 || _contextStack.Count > 0
                    ? (sharedVariables ?? new ParameterCollection())
                    : previousShared;
            // An operation a plugin issues (through its own service) reaches the platform from the sandbox: its
            // PreValidation steps get the extra input FromSandbox = true (verified live; the other stages were not
            // observed to carry it).
            OperationFrame frame = new OperationFrame
            {
                FromSandbox = inner ? outer!.FromSandbox : _contextStack.Count > 0,
                InitialShared = CopyParameters(_sharedVariables),
                BulkMessage = bulkMessage,
                ParentContext = innerOf,
            };
            if (_frames.Count == 0 && !_correlationPinned)
            {
                // A top-level operation gets its own correlation id, shared by everything it cascades into and the
                // async steps it queues, their own writes included (verified live: the cascade and its async steps
                // share one id; each child of an ExecuteMultiple gets its own).
                _correlationId = Guid.NewGuid();
            }

            _frames.Push(frame);
            try
            {
                if (_depth > _maxDepth)
                {
                    throw new InvalidPluginExecutionException(
                        $"Vela pipeline stopped at depth {_depth} (cap {_maxDepth}). Likely infinite recursion between plugins.");
                }

                List<Action> asyncQueue = new List<Action>();
                T result;
                try
                {
                    result = body(asyncQueue);
                }
                catch
                {
                    if (frame.Owned != null)
                    {
                        Rollback(frame.Owned);
                    }

                    throw;
                }

                // Asynchronous steps belong to the transaction: they run after it commits and are discarded if it
                // rolls back. Real Dataverse runs them as decoupled system jobs whose failures never surface to the
                // triggering caller, so their exceptions are recorded (assert via AsyncStepFailures), not thrown.
                // A read is not transactional: outside a transaction its async steps are queued as committed at once.
                PipelineTransaction? transaction = frame.Owned ?? _transaction;
                if (transaction != null)
                {
                    transaction.AsyncSteps.AddRange(asyncQueue);
                }
                else
                {
                    foreach (Action pending in asyncQueue)
                    {
                        _committedAsync.Enqueue(pending);
                    }
                }

                if (frame.Owned != null)
                {
                    Commit(frame.Owned);
                }

                return result;
            }
            finally
            {
                _frames.Pop();
                _sharedVariables = previousShared;
                if (!inner)
                {
                    _depth--;
                }
            }
        }

        // Called by an operation once its PreValidation stage has run: opens the transaction unless one is already
        // open (a nested operation, or a child of an ExecuteTransaction, joins it). The opener owns the commit/rollback.
        private void EnterTransaction()
        {
            if (_transaction != null)
            {
                return;
            }

            PipelineTransaction transaction = new PipelineTransaction(_store.Snapshot());
            _transaction = transaction;
            _frames.Peek().Owned = transaction;
        }

        // Runs several operations as one transaction, opened now so that every operation runs inside it, its
        // PreValidation stage included (ExecuteTransaction). Inside an open transaction the operations simply join it.
        private T RunInOneTransaction<T>(Func<T> body)
        {
            if (_transaction != null)
            {
                return body();
            }

            bool previousPinned = _correlationPinned;
            if (_frames.Count == 0 && !previousPinned)
            {
                // One correlation id for the whole scope (verified live: an ExecuteTransaction batch shares one id).
                _correlationId = Guid.NewGuid();
            }

            _correlationPinned = true;
            PipelineTransaction transaction = _transaction = new PipelineTransaction(_store.Snapshot());
            try
            {
                T result = body();
                Commit(transaction);
                return result;
            }
            catch
            {
                Rollback(transaction);
                throw;
            }
            finally
            {
                _correlationPinned = previousPinned;
            }
        }

        private void Commit(PipelineTransaction transaction)
        {
            _transaction = null;
            foreach (Action pending in transaction.AsyncSteps)
            {
                _committedAsync.Enqueue(pending);
            }
        }

        private void Rollback(PipelineTransaction transaction)
        {
            _transaction = null;
            _store.RestoreTo(transaction.Snapshot);
        }

        // Wraps an asynchronous dispatch so it runs later the way the platform runs the system job: with the context
        // it was queued with (depth, shared variables, identity, parent context, bypass) but outside any transaction.
        private Action Deferred(Action dispatch)
        {
            int depth = _depth;
            ParameterCollection shared = _sharedVariables;
            BypassContext bypass = _bypass;
            Guid? caller = _callerUserId;
            Guid? initiating = _initiatingUserId;
            Guid correlationId = _correlationId;
            Guid requestId = _requestId;
            FakePluginExecutionContext? parent = _contextStack.Count > 0 ? _contextStack.Peek() : null;

            return () =>
            {
                Guid previousCorrelation = _correlationId;
                Guid previousRequest = _requestId;
                Guid previousOperation = _asyncOperationId;
                int previousDepth = _depth;
                ParameterCollection previousShared = _sharedVariables;
                BypassContext previousBypass = _bypass;
                Guid? previousCaller = _callerUserId;
                Guid? previousInitiating = _initiatingUserId;
                PipelineTransaction? previousTransaction = _transaction;
                bool previousPinned = _correlationPinned;
                _depth = depth;
                _sharedVariables = shared;
                _bypass = bypass;
                _callerUserId = caller;
                _initiatingUserId = initiating;
                _transaction = null;
                // The step's own writes keep the operation's correlation id (verified live: the cascade and its async
                // steps share one), although they start with no operation on the call stack.
                _correlationPinned = true;
                _correlationId = correlationId;
                _requestId = requestId;
                _asyncOperationId = Guid.NewGuid();
                if (parent != null)
                {
                    _contextStack.Push(parent);
                }

                // The async step runs as its own system job, outside any transaction: a write it made before failing
                // stays (verified live: the job fails, its created row persists), and the async steps that write
                // queued still run. The failure is recorded in AsyncStepFailures.
                try
                {
                    if (caller == null)
                    {
                        dispatch();
                    }
                    else
                    {
                        _store.RunAs<object?>(caller == SystemUserId ? (Guid?)null : caller, () =>
                        {
                            dispatch();
                            return null;
                        });
                    }
                }
                finally
                {
                    _correlationId = previousCorrelation;
                    _requestId = previousRequest;
                    _asyncOperationId = previousOperation;
                    if (parent != null)
                    {
                        _contextStack.Pop();
                    }

                    _depth = previousDepth;
                    _sharedVariables = previousShared;
                    _bypass = previousBypass;
                    _callerUserId = previousCaller;
                    _initiatingUserId = previousInitiating;
                    _transaction = previousTransaction;
                    _correlationPinned = previousPinned;
                }
            };
        }

        // Every service call (the test's and the plugins') enters here. A write made from inside a transaction that
        // fails ends that transaction; a later call in it is refused. When the outermost call returns, the
        // asynchronous steps of the committed transactions run.
        internal T Invoke<T>(PipelineIdentity identity, Func<T> body, bool isWrite = true)
        {
            PipelineTransaction? enclosing = _transaction;
            if (enclosing?.AbortedBy != null)
            {
                throw TransactionFault(NoActiveTransaction,
                    "There is no active transaction. This error is usually caused by custom plug-ins that ignore errors from service calls and continue processing.",
                    enclosing.AbortedBy);
            }

            if (_callDepth == 0 && !_drainingAsync)
            {
                // A call into the pipeline from outside starts a request: every step it triggers, async ones included,
                // carries its id (verified live: a sync step's OperationId is the request id).
                _requestId = Guid.NewGuid();
            }

            _callDepth++;
            try
            {
                return RunAs(identity, body);
            }
            catch (Exception ex)
            {
                // Only a failed write ends the transaction; a plugin may catch a failed read (a missing row, a missing
                // Read privilege, a row out of reach) and carry on, and so a write that failed its Read check (verified
                // live).
                if (isWrite && !PlatformFault.IsReadCheck(ex) && enclosing != null && enclosing.AbortedBy == null)
                {
                    enclosing.AbortedBy = ex;
                }

                throw;
            }
            finally
            {
                _callDepth--;
                if (_callDepth == 0)
                {
                    RunCommittedAsyncSteps();
                }
            }
        }

        private void RunCommittedAsyncSteps()
        {
            if (_drainingAsync)
            {
                return;
            }

            _drainingAsync = true;
            try
            {
                while (_committedAsync.Count > 0)
                {
                    Action pending = _committedAsync.Dequeue();
                    try
                    {
                        pending();
                    }
                    catch (Exception ex)
                    {
                        _asyncStepFailures.Add(ex);
                    }
                }
            }
            finally
            {
                _drainingAsync = false;
            }
        }

        private static System.ServiceModel.FaultException<OrganizationServiceFault> TransactionFault(int errorCode, string message, Exception cause)
        {
            string full = $"{message} (Vela harness: the failed call was: {cause.Message})";
            return new System.ServiceModel.FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = errorCode, Message = full },
                new System.ServiceModel.FaultReason(full));
        }

        // Runs a call as the identity of the service that issued it (see PipelineOrganizationService): the store
        // enforces security as that user (or not at all for SYSTEM), and plugins fired by the call see that user as
        // ctx.UserId. The test-facing service (Caller) keeps whatever identity is in force. The outermost caller is
        // remembered as the initiating user, which never changes through impersonation or SYSTEM calls.
        internal T RunAs<T>(PipelineIdentity identity, Func<T> body)
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
        private void ExecuteWithContext(FakePluginExecutionContext context, IPlugin plugin, PipelineExecutionEntry entry, StepDefinition? step = null, FakePluginExecutionContext? parentContext = null, bool outsideTransaction = false)
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
            context.ParentContext = parentContext ?? (_contextStack.Count > 0 ? _contextStack.Peek() : null);
            context.IsInTransaction = _transaction != null && !outsideTransaction;

            // Verified live: every step reports the sandbox isolation mode (2) and "none" as SecondaryEntityName; a sync
            // step's OperationId is the request id, an async step's is its own system job id.
            context.RequestId = _requestId;
            context.OperationId = context.Mode == (int)ExecutionMode.Asynchronous ? _asyncOperationId : _requestId;
            context.IsolationMode = 2;
            context.SecondaryEntityName = "none";
            context.EnvironmentId = _environmentId;
            context.TenantId = _tenantId;
            context.IsApplicationUser = _isApplicationUser;

            PipelineTransaction? transaction = _transaction;
            // An operation the plugin issues sees this context as its ParentContext as it was before the plugin ran:
            // the variables the plugin sets are not in it (verified live).
            _contextStack.Push(context.SnapshotForChild());
            try
            {
                if (!(plugin is Vela.Plugin.PluginBase))
                {
                    // A plain IPlugin has no Vela dispatch to report through: its Execute is the handler.
                    entry.MarkHandlerInvoked();
                }

                plugin.Execute(BuildProvider(context, entry));
            }
            finally
            {
                _contextStack.Pop();
            }

            // The plugin returned normally although one of its service calls failed inside the transaction: the
            // platform has already ended the transaction, so it fails the operation instead of letting it commit.
            if (transaction?.AbortedBy is Exception swallowed)
            {
                throw TransactionFault(IsvReducedTransactionCount,
                    "ISV code reduced the open transaction count. Custom plug-ins should not catch exceptions from OrganizationService calls and continue processing.",
                    swallowed);
            }
        }

        private IServiceProvider BuildProvider(FakePluginExecutionContext context, PipelineExecutionEntry entry)
        {
            // ctx.Service is CreateOrganizationService(ctx.UserId): a service acting as that user (subject to the
            // security model). ctx.SystemService is CreateOrganizationService(null): SYSTEM, not subject to it.
            IOrganizationServiceFactory factory = new PipelineServiceFactory(this);

            Dictionary<Type, object> services = new Dictionary<Type, object>
            {
                [typeof(IPluginExecutionContext)] = context,
                [typeof(IOrganizationServiceFactory)] = factory,
                [typeof(ITracingService)] = _trace,
                [typeof(Microsoft.Xrm.Sdk.PluginTelemetry.ILogger)] = _logger,
                // Not a platform service: Vela's runtime reports through it when it calls the handler or skips it, so
                // the log entry can say whether the invoked step's handler ran.
                [typeof(Vela.Context.IHandlerObserver)] = new EntryHandlerObserver(entry),
            };

            if (_managedIdentityService != null)
            {
                services[typeof(IManagedIdentityService)] = _managedIdentityService;
            }

            // Not a platform service: the pipeline's own plug-in cache, on its clock.
            services[typeof(Vela.Caching.PluginCacheService)] = Cache;

            // Not a platform service either: the services the test replaced with WithService, for every plugin it runs.
            if (!_serviceOverrides.IsEmpty)
            {
                services[typeof(Vela.DI.IServiceOverrides)] = _serviceOverrides;
            }

            return new DictionaryServiceProvider(services);
        }

        private sealed class EntryHandlerObserver : Vela.Context.IHandlerObserver
        {
            private readonly PipelineExecutionEntry _entry;

            public EntryHandlerObserver(PipelineExecutionEntry entry) => _entry = entry;

            public void HandlerInvoked() => _entry.MarkHandlerInvoked();

            public void HandlerSkipped(string reason) => _entry.MarkHandlerSkipped(reason);
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
    }
}
