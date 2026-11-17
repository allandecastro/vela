using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Steps;

namespace Vela.Testing
{
    // The bypass directives in force for the current operation. ShouldBypass decides whether a registered
    // step is suppressed (by "all custom", by execution mode, or by step key).
    internal sealed class BypassContext
    {
        public static readonly BypassContext None = new BypassContext();

        public bool AllCustom;
        public bool BypassSync;
        public bool BypassAsync;
        public readonly HashSet<string> StepKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The directives of both contexts: a nested request keeps everything it inherited and adds its own.
        public BypassContext Union(BypassContext other)
        {
            if (!other.AllCustom && !other.BypassSync && !other.BypassAsync && other.StepKeys.Count == 0)
            {
                return this;
            }

            BypassContext union = new BypassContext
            {
                AllCustom = AllCustom || other.AllCustom,
                BypassSync = BypassSync || other.BypassSync,
                BypassAsync = BypassAsync || other.BypassAsync,
            };
            union.StepKeys.UnionWith(StepKeys);
            union.StepKeys.UnionWith(other.StepKeys);
            return union;
        }

        public bool ShouldBypass(StepDefinition step)
        {
            return AllCustom
                || (BypassSync && step.Mode == ExecutionMode.Synchronous)
                || (BypassAsync && step.Mode == ExecutionMode.Asynchronous)
                || (step.StepKey.Length > 0 && StepKeys.Contains(step.StepKey));
        }
    }

    // One row of a Create/Update batch: the mutable Target (shared with the committed entity and the bulk
    // Targets collection) plus its pre/post state and changed attributes for image projection and filtering.
    internal sealed class BatchItem
    {
        public Entity Target = null!;
        public Entity? PreState;
        public Entity? PostState;
        public ISet<string>? ChangedAttributes;

        // Under a CreateMultiple/UpdateMultiple request, the row's own SharedVariables for its Create/Update steps:
        // one at PreValidation (dropped afterwards), one shared by PreOperation and PostOperation (verified live).
        public ParameterCollection? ValidationShared;
        public ParameterCollection? MainShared;
    }

    // One stage firing of one operation: what the selected steps' execution contexts are built from.
    internal sealed class StageInvocation
    {
        public string Message = string.Empty;
        public PluginStages Stage;
        public string EntityLogicalName = string.Empty;
        public Guid PrimaryId;
        public object? Target;
        public Entity? PreState;
        public Entity? PostState;
        // Merge only: the subordinate record (SubordinateId) before and after the merge.
        public Entity? SubordinatePreState;
        public Entity? SubordinatePostState;
        public IDictionary<string, object>? ExtraInputs;
        // For a generic modelled message (no dedicated data simulation): the whole request parameter set to
        // expose as InputParameters, and the response Results to expose as OutputParameters (Post stage).
        public ParameterCollection? AllInputs;
        public ParameterCollection? Outputs;
        // Captured when the stage fires (so a deferred async dispatch sees them too): the operation came from a
        // plugin, the step contexts' own SharedVariables when they differ from the operation's, and their
        // ParentContext when it is not the calling plugin's context.
        public bool FromSandbox;
        // A read's steps report IsInTransaction = false even when the read runs inside a write's transaction.
        public bool OutsideTransaction;
        // A Custom API's asynchronous steps see an empty PrimaryEntityId, whatever the binding (verified live).
        public bool AsyncDropsPrimaryId;
        public ParameterCollection? SharedVariables;
        public FakePluginExecutionContext? ParentContext;
    }

    // The simulated database transaction: the store snapshot to restore on rollback, the asynchronous steps queued
    // in it (run after it commits), and the failure that ended it when a plugin swallowed a failed service call.
    internal sealed class PipelineTransaction
    {
        public PipelineTransaction(InMemoryOrganizationService.StoreSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public InMemoryOrganizationService.StoreSnapshot Snapshot { get; }

        public List<Action> AsyncSteps { get; } = new List<Action>();

        public Exception? AbortedBy { get; set; }
    }

    // One operation on the pipeline's call stack; Owned is the transaction it opened (and so commits or rolls back).
    internal sealed class OperationFrame
    {
        public PipelineTransaction? Owned { get; set; }

        // The operation was issued by a plugin (its PreValidation steps see InputParameters["FromSandbox"] = true).
        public bool FromSandbox { get; set; }

        // Create/Update/Delete: the shared variables of the PreOperation/PostOperation steps (those set at
        // PreValidation are not in them), created on first use, and the stage-30 context that exposes the PreValidation
        // ones as their ParentContext. There is one stage-30 context per operation: a cascaded child's Delete steps get
        // the root's (verified live).
        public ParameterCollection? MainStageShared { get; set; }

        public FakePluginExecutionContext? MainStageContext { get; set; }

        // A CreateMultiple/UpdateMultiple request (not a single Create/Update): its message, and the CreateMultiple /
        // UpdateMultiple stage contexts (by stage) its rows' Create/Update steps get as ParentContext (verified live).
        public string? BulkMessage { get; set; }

        public Dictionary<int, FakePluginExecutionContext> BulkStageContexts { get; } = new Dictionary<int, FakePluginExecutionContext>();

        // The context the operation's own contexts have as ParentContext when it is not the calling plugin's: the
        // Upsert main operation for the Create or Update an Upsert runs (verified live).
        public FakePluginExecutionContext? ParentContext { get; set; }

        // Set when the operation's main operation is a platform Update (Assign, SetState, Merge): the Update steps'
        // stage-30 ParentContext is then this message's main operation, with these inputs (verified live for Assign
        // and Merge).
        public string? MainMessage { get; set; }

        public ParameterCollection? MainInputs { get; set; }

        // The operation's shared variables as they were before its PreValidation stage ran.
        public ParameterCollection? InitialShared { get; set; }
    }
}
