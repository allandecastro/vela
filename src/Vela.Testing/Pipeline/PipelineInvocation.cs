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
        public ISet<string>? ChangedAttributes;
        // For a generic modelled message (no dedicated data simulation): the whole request parameter set to
        // expose as InputParameters, and the response Results to expose as OutputParameters (Post stage).
        public ParameterCollection? AllInputs;
        public ParameterCollection? Outputs;
    }
}
