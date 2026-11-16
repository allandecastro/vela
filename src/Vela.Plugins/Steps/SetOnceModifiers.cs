#nullable enable
namespace Vela.Steps
{
    /// <summary>
    /// The single-value step modifiers: each overwrites the earlier value when called twice, which silently drops it,
    /// so a step may call each at most once. The one list shared, as source, by the step builder (which throws on the
    /// second call) and VELA003 (which flags it at build), so the two cannot drift. <c>Disabled()</c> is not here (an
    /// idempotent flag), nor are the images (several captures with distinct aliases are legitimate).
    /// </summary>
    internal static class SetOnceModifiers
    {
        /// <summary>The fluent method names, in the order of the builder's set-once bits.</summary>
        internal static readonly string[] Methods =
        {
            "FilterOn",
            "WithExecutionOrder",
            "WithDescription",
            "WithStepKey",
            "WithImpersonation",
            "WithSupportedDeployment",
            "WithUnsecureConfig",
            "WithSecureConfigFrom",
            "ForRelationship",
            "SkipWhenDepthAbove",
        };

        /// <summary>Whether <paramref name="method"/> is a set-once modifier (compared ordinally, as C# names are).</summary>
        internal static bool Contains(string method) => System.Array.IndexOf(Methods, method) >= 0;
    }
}
