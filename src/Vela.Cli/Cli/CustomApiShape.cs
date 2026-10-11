namespace Vela.Cli;

/// <summary>
/// Single source of truth for the JSON shape of a Custom API (and its parameters) used by the plan/apply diff.
/// The live read (<see cref="DataverseRegistration"/>), the local normalizer (<see cref="DefinitionNormalizer"/>)
/// and the parameter reader (<see cref="JsonReader"/>) all build the shape through these helpers, so the field set
/// and ordering can never drift between the read side and the normalize side - the divergence bug class that a
/// hand-written shape in three places invites (a one-sided field edit would otherwise diff as a permanent
/// "changed" that apply can never converge). This mirrors what <c>BuildStepShape</c> does for steps.
/// </summary>
internal static class CustomApiShape
{
    public static object Build(
        string pluginTypeName,
        string uniqueName,
        string displayName,
        string description,
        bool isFunction,
        bool isBound,
        string? boundEntityLogicalName,
        string? executePrivilegeName,
        bool isPrivate,
        int allowedCustomProcessingStepType,
        int bindingType,
        bool workflowSdkStepEnabled,
        object[] inputParameters,
        object[] outputParameters)
        => new
        {
            pluginTypeName,
            uniqueName,
            displayName,
            description,
            isFunction,
            isBound,
            boundEntityLogicalName,
            executePrivilegeName,
            isPrivate,
            allowedCustomProcessingStepType,
            bindingType,
            workflowSdkStepEnabled,
            inputParameters,
            outputParameters,
        };

    // uniqueName is part of the shape so a parameter/response-property whose live Dataverse uniquename has drifted
    // from the expected bare name (an API deployed by an older CLI that minted dotted names, which Dataverse cannot
    // serve) is detected as a change and reconciled by apply, instead of hiding behind an otherwise-identical shape.
    // description is the parameter's Dataverse description: the declared one, or the parameter name when none is
    // declared (what apply writes, and what every parameter deployed before descriptions were written holds).
    public static object InputParameter(string name, bool isOptional, string? parameterTypeName, bool isCollection, string? logicalEntityName, string uniqueName, string description)
        => new { name, isOptional, parameterTypeName, isCollection, logicalEntityName, uniqueName, description };

    public static object OutputParameter(string name, string? parameterTypeName, bool isCollection, string? logicalEntityName, string uniqueName, string description)
        => new { name, parameterTypeName, isCollection, logicalEntityName, uniqueName, description };

    /// <summary>The value apply writes for a declared label: the trimmed text, or <paramref name="fallback"/> when blank.</summary>
    public static string LabelOrDefault(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
}
