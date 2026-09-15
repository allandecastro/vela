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

    public static object InputParameter(string name, bool isOptional, string? parameterTypeName, bool isCollection, string? logicalEntityName)
        => new { name, isOptional, parameterTypeName, isCollection, logicalEntityName };

    public static object OutputParameter(string name, string? parameterTypeName, bool isCollection, string? logicalEntityName)
        => new { name, parameterTypeName, isCollection, logicalEntityName };
}
