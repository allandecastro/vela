using Vela.Constants;
using Vela.StepBuilder;

namespace Vela.Testing
{
    /// <summary>
    /// One entry in a <see cref="PluginPipeline"/>'s execution log: a step that fired during simulation.
    /// Useful for asserting which plugins ran, in what order, and at which cascade depth.
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

        public override string ToString()
        {
            return $"{Message}/{Stage} {EntityLogicalName} (depth {Depth}) -> {PluginTypeName}";
        }
    }
}
