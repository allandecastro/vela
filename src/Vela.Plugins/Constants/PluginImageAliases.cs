namespace Vela.Constants
{
    /// <summary>
    /// Standard image aliases used by plugin steps.
    /// </summary>
    public static class PluginImageAliases
    {
        /// <summary>
        /// Default pre-image alias.
        /// </summary>
        public const string PreImage = "PreImage";

        /// <summary>
        /// Default post-image alias.
        /// </summary>
        public const string PostImage = "PostImage";

        /// <summary>
        /// Default alias of a Merge step's pre-image of the subordinate record (the record merged away).
        /// </summary>
        public const string SubordinatePreImage = "SubordinatePreImage";

        /// <summary>
        /// Default alias of a Merge step's post-image of the subordinate record: the deactivated record, whose
        /// <c>masterid</c> points at the master.
        /// </summary>
        public const string SubordinatePostImage = "SubordinatePostImage";
    }
}
