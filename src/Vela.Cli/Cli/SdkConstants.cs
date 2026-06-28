namespace Vela.Cli;

/// <summary>
/// Named Dataverse SDK magic values used while applying registrations, so call sites read
/// intent instead of bare numbers.
/// </summary>
internal static class SdkConstants
{
    /// <summary>
    /// Values of the <c>customapifieldtype</c> / <c>customapirequestparameter.type</c> OptionSet.
    /// </summary>
    internal static class CustomApiParameterType
    {
        internal const int Boolean = 0;
        internal const int DateTime = 1;
        internal const int Decimal = 2;
        internal const int Entity = 3;
        internal const int EntityCollection = 4;
        internal const int EntityReference = 5;
        internal const int Float = 6;
        internal const int Integer = 7;
        internal const int Money = 8;
        internal const int Picklist = 9;
        internal const int String = 10;
        internal const int StringArray = 11;
        internal const int Guid = 12;
    }

    /// <summary>
    /// Values of the SDK message-processing-step <c>stage</c> OptionSet, as <see cref="int"/> for the
    /// registration call sites. Derived from the public <see cref="Vela.Constants.PluginStages"/> enum
    /// (linked into this project as source) so the two are a single source of truth and can't drift.
    /// MainOperation (30) is reserved for the Custom API core operation; there is no stage 50.
    /// </summary>
    internal static class PipelineStage
    {
        internal const int PreValidation = (int)Vela.Constants.PluginStages.PreValidation;
        internal const int PreOperation = (int)Vela.Constants.PluginStages.PreOperation;
        internal const int MainOperation = (int)Vela.Constants.PluginStages.MainOperation;
        internal const int PostOperation = (int)Vela.Constants.PluginStages.PostOperation;
    }
}
