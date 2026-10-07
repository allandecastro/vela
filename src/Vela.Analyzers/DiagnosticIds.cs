namespace Vela
{
    /// <summary>
    /// The Vela diagnostic IDs, in one place. Compiled into Vela.Analyzers and linked into Vela.CodeFixes, so a code
    /// fix names the diagnostic it fixes by constant instead of repeating a string literal.
    /// </summary>
    internal static class DiagnosticIds
    {
        public const string ColumnSelector = "VELA001";
        public const string StepCompletion = "VELA002";
        public const string DuplicateModifier = "VELA003";
        public const string StaticHandler = "VELA004";
        public const string DuplicateColumn = "VELA005";
        public const string PreferTypedMessage = "VELA006";
        public const string ImageStage = "VELA007";
        public const string DuplicateStepTrigger = "VELA008";
        public const string HasChangedPreImage = "VELA009";
        public const string RelationshipColumnSelector = "VELA010";
        public const string MutablePluginState = "VELA011";
        public const string PluginExceptionType = "VELA012";
        public const string RetrieveAllColumns = "VELA013";
        public const string PartialPluginForTypedImage = "VELA014";
        public const string HandlerContextMatch = "VELA015";
        public const string SingleStepPerTypedContextHandler = "VELA016";
        public const string UncapturedImage = "VELA017";
        public const string AsyncTargetWrite = "VELA018";
        public const string FilesystemAccess = "VELA019";
        public const string MissingFilter = "VELA020";
        public const string ImageProjectionNotGenerated = "VELA021";
    }
}
