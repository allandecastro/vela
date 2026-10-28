namespace Vela.Constants
{
    /// <summary>
    /// The lengths Dataverse stores for the labels Vela deploys, and the shape of the step-key marker Vela appends to a
    /// step description. Single source of truth, shared as linked source by the runtime (the automatic step key), the
    /// CLI (the scan-time length checks of <c>vela plan</c>) and the Roslyn analyzer (VELA028), so the build-time
    /// rule and the plan check cannot disagree. Self-contained so it compiles in net462, netstandard2.0 and net10.0.
    /// </summary>
    internal static class DataverseLabelLimits
    {
        /// <summary><c>customapi.displayname</c>.</summary>
        internal const int CustomApiDisplayName = 100;

        /// <summary><c>customapi.description</c>.</summary>
        internal const int CustomApiDescription = 300;

        /// <summary><c>customapirequestparameter.description</c>.</summary>
        internal const int CustomApiRequestParameterDescription = 300;

        /// <summary><c>customapiresponseproperty.description</c>.</summary>
        internal const int CustomApiResponsePropertyDescription = 100;

        /// <summary><c>sdkmessageprocessingstep.description</c>, step-key marker included.</summary>
        internal const int StepDescription = 256;

        /// <summary>The opening of the step-key marker Vela appends, on its own line, to a step description.</summary>
        internal const string StepKeyMarkerPrefix = "[vela:stepkey=";

        /// <summary>The closing of the step-key marker.</summary>
        internal const string StepKeyMarkerSuffix = "]";

        /// <summary>The prefix of the step key Vela gives a step that declares none (<c>WithStepKey</c>).</summary>
        internal const string AutoStepKeyPrefix = "dv-";

        /// <summary>The number of hexadecimal characters after <see cref="AutoStepKeyPrefix"/> in an automatic step key.</summary>
        internal const int AutoStepKeyHashLength = 12;

        /// <summary>
        /// The characters the step-key marker of <paramref name="stepKeyLength"/> adds to a step description: the line
        /// break before it, then <c>[vela:stepkey=</c>, the key and <c>]</c>.
        /// </summary>
        internal static int StepKeyMarkerLength(int stepKeyLength)
            => 1 + StepKeyMarkerPrefix.Length + stepKeyLength + StepKeyMarkerSuffix.Length;
    }
}
