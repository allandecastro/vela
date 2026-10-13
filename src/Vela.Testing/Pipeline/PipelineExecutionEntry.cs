using Vela.Constants;
using Vela.Steps;

namespace Vela.Testing
{
    /// <summary>
    /// One entry in a <see cref="PluginPipeline"/>'s execution log: a step the platform invoked during simulation.
    /// Useful for asserting which plugins were invoked, in what order, at which cascade depth, and (through
    /// <see cref="HandlerInvoked"/>) whether their handler actually ran.
    /// </summary>
    public sealed class PipelineExecutionEntry
    {
        public PipelineExecutionEntry(string pluginTypeName, string message, PluginStages stage, ExecutionMode mode, string entityLogicalName, int depth)
        {
            PluginTypeName = pluginTypeName;
            Message = message;
            Stage = stage;
            Mode = mode;
            EntityLogicalName = entityLogicalName;
            Depth = depth;
        }

        /// <summary>Full name of the plugin type that executed.</summary>
        public string PluginTypeName { get; }

        /// <summary>The message (Create, Update, Delete, Associate, or a Custom API unique name).</summary>
        public string Message { get; }

        /// <summary>The pipeline stage.</summary>
        public PluginStages Stage { get; }

        /// <summary>The execution mode (synchronous or asynchronous).</summary>
        public ExecutionMode Mode { get; }

        /// <summary>The primary entity logical name (empty for global Custom APIs).</summary>
        public string EntityLogicalName { get; }

        /// <summary>The cascade depth at which the step fired (1 = top-level operation).</summary>
        public int Depth { get; }

        /// <summary>
        /// Whether the step's handler was called. An entry means the platform invoked the step; the handler is not
        /// called when Vela's runtime skips it (a <c>.ForRelationship(...)</c> scope that does not match,
        /// <c>.SkipWhenDepthAbove(n)</c>, the plugin-level depth guard; <see cref="SkipReason"/> then says why) or when
        /// the execution fails before reaching it (the plug-in's constructor, its service registration, the request
        /// mapping). For a plug-in that does not derive from <c>PluginBase</c>, its <c>Execute</c> is the handler.
        /// </summary>
        public bool HandlerInvoked { get; private set; }

        /// <summary>
        /// Why Vela's runtime skipped the handler (the line it traced), or <c>null</c> when it did not skip it.
        /// </summary>
        public string? SkipReason { get; private set; }

        internal void MarkHandlerInvoked() => HandlerInvoked = true;

        internal void MarkHandlerSkipped(string reason) => SkipReason = reason;

        public override string ToString()
        {
            string entry = $"{Message}/{Stage} {EntityLogicalName} (depth {Depth}) -> {PluginTypeName}";
            if (HandlerInvoked)
            {
                return entry;
            }

            return SkipReason == null ? $"{entry} [handler not run]" : $"{entry} [handler not run: {SkipReason}]";
        }
    }
}
