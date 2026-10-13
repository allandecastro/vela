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
        /// Whether the step's handler ran. An entry means the platform invoked the step; Vela's runtime can still decide
        /// not to call the handler (a <c>.ForRelationship(...)</c> scope that does not match, <c>.SkipWhenDepthAbove(n)</c>,
        /// the plugin-level depth guard), and then this is <c>false</c> and <see cref="SkipReason"/> says why.
        /// </summary>
        public bool HandlerInvoked => SkipReason == null;

        /// <summary>
        /// Why Vela's runtime did not call the handler (the line it traced), or <c>null</c> when the handler ran.
        /// </summary>
        public string? SkipReason { get; private set; }

        internal void MarkHandlerSkipped(string reason) => SkipReason = reason;

        public override string ToString()
        {
            string entry = $"{Message}/{Stage} {EntityLogicalName} (depth {Depth}) -> {PluginTypeName}";
            return HandlerInvoked ? entry : $"{entry} [handler not run: {SkipReason}]";
        }
    }
}
