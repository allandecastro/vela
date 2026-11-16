using System;
using System.Collections.Concurrent;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Retry;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // #438: async steps run only once the outermost operation commits (live, a rolled-back operation creates no async
    // job), and IsInTransaction follows the stage and mode as on a real org.

    // Account post-create, rank 1: creates a task, whose own steps then fire at depth 2.
    public sealed class AtxCreateTask : EntityPluginBase<FakeAccount>
    {
        public AtxCreateTask(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(1)
                .Handle(ctx => ctx.SystemService.Create(new FakeTask { Subject = "child" }));
    }

    // Account post-create, rank 2: fails after AtxCreateTask has written its task.
    public sealed class AtxThrowAfterTask : EntityPluginBase<FakeAccount>
    {
        public AtxThrowAfterTask(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(2)
                .Handle(_ => throw new PluginBusinessException("outer fails"));
    }

    // Account post-create, rank 2: succeeds, so its log entry tells whether the task's async step ran before it.
    public sealed class AtxSyncAfterTask : EntityPluginBase<FakeAccount>
    {
        public AtxSyncAfterTask(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(2)
                .Handle(_ => { });
    }

    // Task post-create, async: writes a note.
    public sealed class AtxAsyncNoteOnTask : EntityPluginBase<FakeTask>
    {
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        public AtxAsyncNoteOnTask(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Async().FilterOn(t => t.Subject)
                .Handle(ctx =>
                {
                    Seen.Enqueue($"d{ctx.Depth}");
                    ctx.SystemService.Create(new Entity("annotation") { ["subject"] = "async-note" });
                });
    }

    // Account post-create, async: writes a note, then fails; the note stays (no transaction around an async step).
    public sealed class AtxAsyncWriteThenThrow : EntityPluginBase<FakeAccount>
    {
        public AtxAsyncWriteThenThrow(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name)
                .Handle(ctx =>
                {
                    ctx.SystemService.Create(new Entity("annotation") { ["subject"] = "kept" });
                    throw new PluginBusinessException("async fails");
                });
    }

    // Records IsInTransaction on every stage and mode of an account create.
    public sealed class AtxRecordAccountTransaction : EntityPluginBase<FakeAccount>
    {
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        public AtxRecordAccountTransaction(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => Seen.Enqueue($"account:s10:sync:d{ctx.Depth}:{ctx.IsInTransaction}"));
            RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => Seen.Enqueue($"account:s20:sync:d{ctx.Depth}:{ctx.IsInTransaction}"));
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => Seen.Enqueue($"account:s40:sync:d{ctx.Depth}:{ctx.IsInTransaction}"));
            RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name)
                .Handle(ctx => Seen.Enqueue($"account:s40:async:d{ctx.Depth}:{ctx.IsInTransaction}"));
        }
    }

    // Records IsInTransaction on the PreValidation of a task create.
    public sealed class AtxRecordTaskPreValidation : EntityPluginBase<FakeTask>
    {
        public AtxRecordTaskPreValidation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreValidation().Sync().FilterOn(t => t.Subject)
                .Handle(ctx => AtxRecordAccountTransaction.Seen.Enqueue($"task:s10:sync:d{ctx.Depth}:{ctx.IsInTransaction}"));
    }

    // Account post-create, async: a throttled call is retried outside a transaction.
    public sealed class AtxRetryAsync : EntityPluginBase<FakeAccount>
    {
        public AtxRetryAsync(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name)
                .Handle(ctx => RetryPolicy.Execute(ctx, AtxThrottle.ThrottleOnce, initialDelayMs: 0));
    }

    // Account post-create, sync: the same throttled call runs once inside the transaction.
    public sealed class AtxRetrySync : EntityPluginBase<FakeAccount>
    {
        public AtxRetrySync(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => RetryPolicy.Execute(ctx, AtxThrottle.ThrottleOnce, initialDelayMs: 0));
    }

    internal static class AtxThrottle
    {
        public static int Attempts;

        // Throttled on the first attempt, succeeds after.
        public static int ThrottleOnce()
        {
            if (++Attempts == 1)
            {
                throw new FaultException<OrganizationServiceFault>(
                    new OrganizationServiceFault { ErrorCode = -2147015902, Message = "Number of requests exceeded the limit" },
                    "Number of requests exceeded the limit");
            }

            return Attempts;
        }
    }

    // Records the correlation id of the sync and async post-create steps.
    public sealed class AtxRecordCorrelation : EntityPluginBase<FakeAccount>
    {
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        public AtxRecordCorrelation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => Seen.Enqueue($"{ctx.GetTarget<FakeAccount>().Name}:sync:{ctx.CorrelationId}"));
            RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name)
                .Handle(ctx => Seen.Enqueue($"{ctx.GetTarget<FakeAccount>().Name}:async:{ctx.CorrelationId}"));
        }
    }

    // Account pre-create: changes the Target, which must reach the store but not the caller's entity.
    public sealed class AtxStampTarget : EntityPluginBase<FakeAccount>
    {
        public AtxStampTarget(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => ctx.GetTarget<FakeAccount>().NumberOfEmployees = 7);
    }

    // Records the raw context fields of every account-create stage, keyed by the record name.
    public sealed class AtxRecordContextFields : EntityPluginBase<FakeAccount>
    {
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        public AtxRecordContextFields(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx => Record(ctx, "s10"));
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => Record(ctx, "s40"));
            RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name).Handle(ctx => Record(ctx, "async"));
        }

        private static void Record(ILocalPluginContext ctx, string stage)
        {
            IPluginExecutionContext raw = ctx.PluginExecutionContext;
            Seen.Enqueue(string.Join("|",
                ((Entity)raw.InputParameters["Target"]).GetAttributeValue<string>("name"),
                stage,
                raw.IsInTransaction,
                raw.CorrelationId,
                raw.RequestId,
                raw.OperationId,
                raw.IsolationMode,
                raw.SecondaryEntityName));
        }

        // The recorded fields of one (name, stage): tx, correlation, request, operation, isolation, secondary.
        public static string[] Of(string name, string stage)
            => Seen.Select(entry => entry.Split('|')).Single(fields => fields[0] == name && fields[1] == stage).Skip(2).ToArray();
    }

    public sealed class AsyncTransactionFidelityTests
    {
        [Fact]
        public void An_async_step_queued_by_a_nested_write_does_not_run_when_the_outer_operation_rolls_back()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<AtxCreateTask>()
                .Register<AtxThrowAfterTask>()
                .Register<AtxAsyncNoteOnTask>();

            Assert.ThrowsAny<Exception>(() => pipeline.Service.Create(new FakeAccount { Name = "Zava" }));

            Assert.Empty(pipeline.GetAll("account"));
            Assert.Empty(pipeline.GetAll("task"));
            Assert.Empty(pipeline.GetAll("annotation"));
            Assert.DoesNotContain(pipeline.ExecutionLog, entry => entry.Mode == ExecutionMode.Asynchronous);
            Assert.Empty(pipeline.AsyncStepFailures);
        }

        [Fact]
        public void An_async_step_queued_by_a_nested_write_runs_after_the_outer_operation_commits()
        {
            while (AtxAsyncNoteOnTask.Seen.TryDequeue(out _)) { }
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<AtxCreateTask>()
                .Register<AtxSyncAfterTask>()
                .Register<AtxAsyncNoteOnTask>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            // The task's async step runs after the account's last sync step, not inside the nested create, and keeps
            // the depth of the write that queued it.
            PipelineExecutionEntry last = pipeline.ExecutionLog.Last();
            Assert.Equal(ExecutionMode.Asynchronous, last.Mode);
            Assert.Equal("task", last.EntityLogicalName);
            Assert.Equal(new[] { "d2" }, AtxAsyncNoteOnTask.Seen.ToArray());
            Assert.Single(pipeline.GetAll("annotation"));
        }

        [Fact]
        public void ExecuteTransaction_drops_the_async_steps_of_children_it_rolls_back()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<AtxAsyncNoteOnTask>()
                .Register<ThrowOnPostCreate>();

            ExecuteTransactionRequest batch = new ExecuteTransactionRequest { Requests = new OrganizationRequestCollection() };
            batch.Requests.Add(new CreateRequest { Target = new FakeTask { Subject = "first" } });   // queues an async step
            batch.Requests.Add(new CreateRequest { Target = new FakeAccount { Name = "boom" } });    // ThrowOnPostCreate throws

            Assert.ThrowsAny<Exception>(() => pipeline.Service.Execute(batch));

            Assert.Empty(pipeline.GetAll("task"));
            Assert.Empty(pipeline.GetAll("annotation"));
            Assert.DoesNotContain(pipeline.ExecutionLog, entry => entry.Mode == ExecutionMode.Asynchronous);
        }

        [Fact]
        public void ExecuteTransaction_runs_the_async_steps_once_the_whole_batch_commits()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<AtxAsyncNoteOnTask>()
                .Register<AtxSyncAfterTask>();

            ExecuteTransactionRequest batch = new ExecuteTransactionRequest { Requests = new OrganizationRequestCollection() };
            batch.Requests.Add(new CreateRequest { Target = new FakeTask { Subject = "first" } });
            batch.Requests.Add(new CreateRequest { Target = new FakeAccount { Name = "second" } });

            pipeline.Service.Execute(batch);

            // The first child's async step waits for the second child to commit.
            PipelineExecutionEntry last = pipeline.ExecutionLog.Last();
            Assert.Equal(ExecutionMode.Asynchronous, last.Mode);
            Assert.Equal("task", last.EntityLogicalName);
            Assert.Single(pipeline.GetAll("annotation"));
        }

        [Fact]
        public void A_failed_async_step_keeps_the_writes_it_made_before_failing()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<AtxAsyncWriteThenThrow>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            // Verified live: an async step runs outside any transaction, so the row it created before throwing stays.
            Assert.Single(pipeline.GetAll("account"));      // the triggering operation committed
            Assert.Equal("kept", Assert.Single(pipeline.GetAll("annotation")).GetAttributeValue<string>("subject"));
            Exception failure = Assert.Single(pipeline.AsyncStepFailures);
            Assert.Contains("async fails", failure.Message);
        }

        [Fact]
        public void IsInTransaction_follows_the_stage_mode_and_nesting()
        {
            while (AtxRecordAccountTransaction.Seen.TryDequeue(out _)) { }
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<AtxRecordAccountTransaction>()
                .Register<AtxCreateTask>()
                .Register<AtxRecordTaskPreValidation>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            // Verified live: a depth-1 PreValidation runs before the transaction, a nested one inside it, and an
            // async step outside any.
            Assert.Equal(
                new[]
                {
                    "account:s10:sync:d1:False",
                    "account:s20:sync:d1:True",
                    "account:s40:sync:d1:True",
                    "task:s10:sync:d2:True",
                    "account:s40:async:d1:False",
                },
                AtxRecordAccountTransaction.Seen.ToArray());
        }

        [Fact]
        public void RetryPolicy_retries_a_throttled_call_in_an_async_step()
        {
            AtxThrottle.Attempts = 0;
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AtxRetryAsync>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.Equal(2, AtxThrottle.Attempts);
            Assert.Empty(pipeline.AsyncStepFailures);
        }

        [Fact]
        public void RetryPolicy_does_not_retry_inside_the_transaction_of_a_sync_step()
        {
            AtxThrottle.Attempts = 0;
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AtxRetrySync>();

            Assert.ThrowsAny<Exception>(() => pipeline.Service.Create(new FakeAccount { Name = "Zava" }));

            Assert.Equal(1, AtxThrottle.Attempts);
            Assert.Empty(pipeline.GetAll("account"));
        }

        [Fact]
        public void Each_top_level_operation_has_its_own_correlation_id_shared_with_its_async_steps()
        {
            while (AtxRecordCorrelation.Seen.TryDequeue(out _)) { }
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AtxRecordCorrelation>();

            pipeline.Service.Create(new FakeAccount { Name = "A" });
            pipeline.Service.Create(new FakeAccount { Name = "B" });

            string[] seen = AtxRecordCorrelation.Seen.ToArray();
            string Id(string prefix) => seen.Single(entry => entry.StartsWith(prefix, StringComparison.Ordinal)).Substring(prefix.Length);
            Assert.Equal(Id("A:sync:"), Id("A:async:"));
            Assert.Equal(Id("B:sync:"), Id("B:async:"));
            Assert.NotEqual(Id("A:sync:"), Id("B:sync:"));
        }

        // Verified live 2026-10-09: a sync step's OperationId is the request id; an async step keeps the request id but
        // has its own OperationId (its system job); every step reports IsolationMode 2 (sandbox) and SecondaryEntityName
        // "none".
        [Fact]
        public void The_context_carries_the_request_and_operation_ids_isolation_mode_and_secondary_entity()
        {
            while (AtxRecordContextFields.Seen.TryDequeue(out _)) { }
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AtxRecordContextFields>();

            pipeline.Service.Create(new FakeAccount { Name = "A" });
            pipeline.Service.Create(new FakeAccount { Name = "B" });

            string[] pre = AtxRecordContextFields.Of("A", "s10");
            string[] post = AtxRecordContextFields.Of("A", "s40");
            string[] async = AtxRecordContextFields.Of("A", "async");
            Assert.Equal(pre[2], post[2]);              // one request id per call
            Assert.Equal(post[2], post[3]);             // sync: OperationId = RequestId
            Assert.Equal(post[2], async[2]);            // async keeps the request id...
            Assert.NotEqual(async[2], async[3]);        // ...but has its own operation id
            Assert.NotEqual(post[2], AtxRecordContextFields.Of("B", "s40")[2]);
            Assert.All(new[] { pre, post, async }, fields =>
            {
                Assert.Equal("2", fields[4]);
                Assert.Equal("none", fields[5]);
            });
        }

        // Verified live 2026-10-09: the children of an ExecuteTransaction run in its transaction (PreValidation
        // included) and share one correlation id; the children of an ExecuteMultiple are separate transactions with
        // their own correlation ids, but one request id.
        [Fact]
        public void ExecuteTransaction_children_share_the_transaction_and_correlation_id()
        {
            while (AtxRecordContextFields.Seen.TryDequeue(out _)) { }
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AtxRecordContextFields>();
            ExecuteTransactionRequest batch = new ExecuteTransactionRequest { Requests = new OrganizationRequestCollection() };
            batch.Requests.Add(new CreateRequest { Target = new FakeAccount { Name = "one" } });
            batch.Requests.Add(new CreateRequest { Target = new FakeAccount { Name = "two" } });

            pipeline.Service.Execute(batch);

            string[] one = AtxRecordContextFields.Of("one", "s10");
            string[] two = AtxRecordContextFields.Of("two", "s10");
            Assert.Equal("True", one[0]);
            Assert.Equal("True", two[0]);
            Assert.Equal(one[1], two[1]);
            Assert.Equal(one[2], two[2]);
        }

        [Fact]
        public void ExecuteMultiple_children_have_their_own_transaction_and_correlation_id_but_one_request_id()
        {
            while (AtxRecordContextFields.Seen.TryDequeue(out _)) { }
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AtxRecordContextFields>();
            ExecuteMultipleRequest batch = new ExecuteMultipleRequest
            {
                Requests = new OrganizationRequestCollection(),
                Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = true },
            };
            batch.Requests.Add(new CreateRequest { Target = new FakeAccount { Name = "one" } });
            batch.Requests.Add(new CreateRequest { Target = new FakeAccount { Name = "two" } });

            pipeline.Service.Execute(batch);

            string[] one = AtxRecordContextFields.Of("one", "s10");
            string[] two = AtxRecordContextFields.Of("two", "s10");
            Assert.Equal("False", one[0]);
            Assert.Equal("False", two[0]);
            Assert.NotEqual(one[1], two[1]);
            Assert.Equal(one[2], two[2]);
            Assert.Equal(one[1], AtxRecordContextFields.Of("one", "async")[1]);
        }

        [Fact]
        public void Create_leaves_the_callers_entity_untouched()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AtxStampTarget>();
            FakeAccount account = new FakeAccount { Name = "Zava" };

            Guid id = pipeline.Service.Create(account);

            // Live, the request is serialized: the created id and the plugin's change reach the store only.
            Assert.Equal(Guid.Empty, account.Id);
            Assert.Null(account.NumberOfEmployees);
            Entity stored = Assert.Single(pipeline.GetAll("account"));
            Assert.Equal(id, stored.Id);
            Assert.Equal(7, stored.GetAttributeValue<int?>("numberofemployees"));
        }

        [Fact]
        public void CreateMultiple_leaves_the_callers_entities_untouched()
        {
            PluginPipeline pipeline = PluginPipeline.Empty();
            FakeAccount first = new FakeAccount { Name = "A" };
            FakeAccount second = new FakeAccount { Name = "B" };

            CreateMultipleResponse response = (CreateMultipleResponse)pipeline.Service.Execute(new CreateMultipleRequest
            {
                Targets = new EntityCollection(new Entity[] { first, second }) { EntityName = "account" },
            });

            Assert.Equal(2, response.Ids.Length);
            Assert.Equal(Guid.Empty, first.Id);
            Assert.Equal(Guid.Empty, second.Id);
        }

        [Fact]
        public void Update_leaves_the_callers_entity_untouched()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<AtxStampTargetOnUpdate>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });
            FakeAccount update = new FakeAccount { Id = id, Name = "New" };

            pipeline.Service.Update(update);

            Assert.False(update.Contains("numberofemployees"));
            Assert.Equal(7, Assert.Single(pipeline.GetAll("account")).GetAttributeValue<int?>("numberofemployees"));
        }
    }

    // Account pre-update: changes the Target, which must reach the store but not the caller's entity.
    public sealed class AtxStampTargetOnUpdate : EntityPluginBase<FakeAccount>
    {
        public AtxStampTargetOnUpdate(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => ctx.GetTarget<FakeAccount>().NumberOfEmployees = 7);
    }
}
