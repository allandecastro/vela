namespace Vela.Context
{
    /// <summary>How much an execution snapshot (<see cref="PluginContextExtensions.TraceExecutionSnapshot"/>) writes.</summary>
    public enum TraceSnapshotDetail
    {
        /// <summary>Message, stage, ids, and the names of parameters, images and attributes: no record values.</summary>
        Metadata = 0,

        /// <summary>Everything in <see cref="Metadata"/> plus every parameter and attribute value.</summary>
        Full = 1,

        /// <summary>
        /// No snapshot at all, to save the plug-in trace log budget (about 10 KB per execution) for the plugin's own
        /// traces. The one-line "Executing ..." trace still records the message, stage, mode and depth.
        /// </summary>
        None = 2,
    }
}
