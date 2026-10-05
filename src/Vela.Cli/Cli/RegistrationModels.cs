using System;
using System.Linq;
using System.Text.Json;

namespace Vela.Cli;

public sealed class DataverseConnectionException : Exception
{
    public DataverseConnectionException(string message) : base(message) { }
    public DataverseConnectionException(string message, Exception innerException) : base(message, innerException) { }
}
public sealed record SharedOptions(string[] PackagePaths, string? PackageAssembly, string? ConnectionString, string? OAuthUrl, string? OAuthToken, string? SolutionUniqueName, string? Environment, string? OutPath, bool AutoApprove, bool Interactive, bool Strict, bool Verbose, string? WebResourcesRoot = null, bool Json = false, int? TimeoutMinutes = null, int? BatchChunkSize = null);
public sealed record ExtractedPackage(string PackagePath, string TempDirectory, string PrimaryAssemblyPath, string[] RegistrableAssemblyPaths);
public sealed record AssemblyPayload(string AssemblyName, string AssemblyPath);
public sealed record PluginPackagePayload(string PackageName, string Version, string PackagePath);
public sealed record RegistrationItem(string Kind, string Type, JsonElement Definition);
// SecureConfigByStepKey carries each step's RAW secure config keyed by stepKey. The step definitions in Items have
// their secure config redacted to a hash (so it never lands in plan/inspect output); apply reads the real value
// from this side channel instead, so the plaintext secret is written to Dataverse rather than the digest. It is
// only populated for the local package scan; the live/dataverse state leaves it null.
public sealed record RegistrationState(string[] SourceAssemblies, AssemblyPayload[] SourceAssemblyPayloads, PluginPackagePayload[] PackagePayloads, DateTime GeneratedAtUtc, RegistrationItem[] Items, string[] Diagnostics, IReadOnlyDictionary<string, string>? SecureConfigByStepKey = null);
public sealed record RegistrationDiff(string[] LocalAssemblies, string[] CurrentAssemblies, RegistrationItem[] Added, RegistrationItem[] Removed, RegistrationChange[] Changed, RegistrationItem[] LocalItems, RegistrationItem[] CurrentItems)
{
    public DiffSummary Summary { get; init; } = new DiffSummary(Added.Length, Removed.Length, Changed.Length);
}

public sealed record RegistrationChange(string Kind, string Type, JsonElement Before, JsonElement After);
public sealed record DiffSummary(int Added, int Removed, int Changed);
public sealed record PlanPreview(string Mode, string? Environment, bool HasChanges, DiffSummary Summary, ApplyOperation[] Operations, RegistrationDiff Diff, string[] Diagnostics, string[] Notes, ExecutedCounts? Executed = null);

/// <summary>
/// What an <c>apply</c> actually DID, taken from the pre-apply diff - distinct from <see cref="PlanPreview.Summary"/>,
/// which after a successful apply is the post-apply remaining diff (all zero). Present only on the <c>apply-execute</c>
/// JSON so a script/agent reads the executed counts directly instead of parsing the free-text notes.
/// </summary>
public sealed record ExecutedCounts(int Created, int Updated, int Deleted, bool PackageRepublished, ExecutedByKind[] ByKind)
{
    /// <summary>Builds the executed counts (totals + per-kind breakdown) from the PRE-apply diff.</summary>
    public static ExecutedCounts FromDiff(RegistrationDiff diff, bool packageRepublished)
    {
        string[] kinds = diff.Added.Select(i => i.Kind)
            .Concat(diff.Changed.Select(c => c.Kind))
            .Concat(diff.Removed.Select(i => i.Kind))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        ExecutedByKind[] byKind = kinds.Select(kind => new ExecutedByKind(
            kind,
            diff.Added.Count(i => i.Kind == kind),
            diff.Changed.Count(c => c.Kind == kind),
            diff.Removed.Count(i => i.Kind == kind))).ToArray();

        return new ExecutedCounts(diff.Added.Length, diff.Changed.Length, diff.Removed.Length, packageRepublished, byKind);
    }
}

/// <summary>The executed create/update/delete counts for one component kind (step / custom-api / web-resource).</summary>
public sealed record ExecutedByKind(string Kind, int Created, int Updated, int Deleted);
public sealed record ApplyOperation(string Action, string Kind, string Type, string Reason);
/// <summary>A plugin package that a plan/apply would write: created when absent from Dataverse, otherwise
/// republished because its content differs. Packages whose deployed content already matches are not reported.
/// Lets <c>plan</c> surface a code-only rebuild (unchanged steps/custom APIs, rebuilt assembly) that the
/// step/custom-API diff alone cannot see.</summary>
public sealed record PackagePublishAction(string PackageName, bool IsCreate);
public sealed record StepApplyDefinition(string PluginTypeName, string Message, string EntityLogicalName, string StepKey, string HandlerMethodName, int Stage, int Mode, int ExecutionOrder, string[] FilteringAttributes, ImageApplyDefinition[] PreImages, ImageApplyDefinition[] PostImages, bool Enabled, string UnsecureConfig, string SecureConfig, Guid ImpersonatingUserId = default, int SupportedDeployment = 0);
public sealed record ImageApplyDefinition(string Alias, string[] Attributes);
public sealed record StepImageApplyDefinition(string Alias, string[] Attributes, int ImageType);
public sealed record CustomApiApplyDefinition(string PluginTypeName, string UniqueName, bool IsFunction, bool IsBound, string? BoundEntityLogicalName, string? ExecutePrivilegeName, bool IsPrivate, int AllowedCustomProcessingStepType, int BindingType, bool WorkflowSdkStepEnabled, CustomApiParameterApplyDefinition[] InputParameters, CustomApiParameterApplyDefinition[] OutputParameters);
public sealed record CustomApiParameterApplyDefinition(string Name, bool IsOptional, string? ParameterTypeName, bool IsCollection, string? LogicalEntityName, string UniqueName = "");
