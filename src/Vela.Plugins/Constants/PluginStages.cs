namespace Vela.Constants
{
    /// <summary>
    /// Dataverse plugin pipeline stages.
    /// </summary>
    public enum PluginStages
    {
        /// <summary>
        /// Pre-validation stage.
        /// </summary>
        PreValidation = 10,

        /// <summary>
        /// Pre-operation stage.
        /// </summary>
        PreOperation = 20,

        /// <summary>
        /// Main operation stage (used for Custom APIs).
        /// </summary>
        MainOperation = 30,

        /// <summary>
        /// Post-operation stage.
        /// </summary>
        PostOperation = 40
    }
}
