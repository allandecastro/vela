using System;
using System.Linq;
using System.Text.Json;

namespace Vela.Cli;

public sealed class DataverseConnectionException : Exception
{
    public DataverseConnectionException(string message) : base(message) { }
    public DataverseConnectionException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>
    /// True when <paramref name="exception"/> means Dataverse could not be reached: a failed connect, or a network
    /// error (DNS, refused or reset socket, HTTP transport, timeout) on any later call, which the SDK surfaces as
    /// itself or wrapped. Commands map it to <see cref="CliExitCodes.ExitConnectionFailure"/> (6), so a connection
    /// drop while reading the live state is not reported as a generic failure (4). A Dataverse fault (the service
    /// answered) is not a connection failure.
    /// </summary>
    public static bool IsConnectionFailure(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is System.ServiceModel.FaultException)
            {
                return false;
            }

            if (current is DataverseConnectionException
                or System.Net.Http.HttpRequestException
                or System.Net.Sockets.SocketException
                or System.Net.WebException
                or TimeoutException
                or System.ServiceModel.CommunicationException)
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsConnectionFailure))
            {
                return true;
            }
        }

        return false;
    }
}
public sealed record SharedOptions(string[] PackagePaths, string? PackageAssembly, string? ConnectionString, string? OAuthUrl, DataverseTokenProvider? OAuthToken, string? SolutionUniqueName, string? Environment, string? OutPath, bool AutoApprove, bool Interactive, bool Strict, bool Verbose, string? WebResourcesRoot = null, bool Json = false, int? TimeoutMinutes = null, int? BatchChunkSize = null, string[]? SecureConfigs = null, bool DetailedExitCode = false, Guid? ManagedIdentityId = null)
{
    // The compiler-generated record ToString prints every member, including the connection string (which can embed a
    // client secret), the token provider and inline --secure-config values. Any interpolation or log of the options
    // would leak them, so the secret-bearing members print only whether they are set.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"PackagePaths = [{string.Join(", ", PackagePaths)}], PackageAssembly = {PackageAssembly}, ");
        builder.Append($"ConnectionString = {(ConnectionString == null ? "null" : "***")}, OAuthUrl = {OAuthUrl}, ");
        builder.Append($"OAuthToken = {(OAuthToken == null ? "null" : "***")}, SolutionUniqueName = {SolutionUniqueName}, ");
        builder.Append($"Environment = {Environment}, OutPath = {OutPath}, AutoApprove = {AutoApprove}, Interactive = {Interactive}, ");
        builder.Append($"Strict = {Strict}, Verbose = {Verbose}, WebResourcesRoot = {WebResourcesRoot}, Json = {Json}, ");
        builder.Append($"TimeoutMinutes = {TimeoutMinutes}, BatchChunkSize = {BatchChunkSize}, ");
        builder.Append($"SecureConfigs = {(SecureConfigs == null ? "null" : $"{SecureConfigs.Length} value(s), redacted")}, ");
        builder.Append($"DetailedExitCode = {DetailedExitCode}, ManagedIdentityId = {ManagedIdentityId}");
        return true;
    }
}
public sealed record ExtractedPackage(string PackagePath, string TempDirectory, string PrimaryAssemblyPath, string[] RegistrableAssemblyPaths);
public sealed record AssemblyPayload(string AssemblyName, string AssemblyPath);
// HasRegistrations (local scan): false when the package holds no step, Custom API or unmanaged plugin type (a freshly
// scaffolded, still empty project). Such a package is not created in Dataverse; one that already exists is republished.
// AssemblyName (local scan): the package's plugin assembly, the one Vela scans. Dataverse registers a package's
// assemblies when the package is created and never changes that set on update (verified live), so plan/apply compare it
// with the deployed package's assemblies (DataverseRegistration.FindPackageAssemblySetChanges). Null when unknown (a
// payload built by hand), which skips the added-assembly half of that check.
public sealed record PluginPackagePayload(string PackageName, string Version, string PackagePath, bool HasRegistrations = true, string? AssemblyName = null);
public sealed record RegistrationItem(string Kind, string Type, JsonElement Definition)
{
    /// <summary>The row id of a live step (the live read sets it). Apply deletes a removed step by this id, never by a
    /// lookup that could match another row. Not part of the diff or the JSON output.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? LiveId { get; init; }
}
// Local package scan only (the live state leaves both null), keyed by full step key (DiffEngine.GetStepKey):
// SecureConfigNameByStepKey is the secure configuration NAME each step declares (WithSecureConfigFrom);
// SecureConfigByStepKey is the RAW value SecureConfigResolver resolved for it from --secure-config or the environment.
// Step definitions in Items only ever carry a digest, so a secret never lands in plan/inspect output; apply writes
// the raw value, and only for the steps present here (every other secure configuration is left untouched).
// TypedCustomApiUniqueNames (local scan only) lists the Custom APIs whose code declares a request class
// (CustomApiBase<TRequest, TResponse>); a lightweight one (RegisterCustomApi(name, handler)) has none. A side list, not
// part of the custom-api item shape, so it never enters the plan/apply diff; `vela models` reads it.
// PluginTypeNames (local scan only) lists every class of the scanned assemblies that Dataverse registers as a plugin
// type (Vela plugins and Custom APIs, and the unmanaged IPlugin / CodeActivity classes): a live plugin type missing
// from it is removed by the package update. Null when unknown (a state built by hand), which skips that check.
public sealed record RegistrationState(string[] SourceAssemblies, AssemblyPayload[] SourceAssemblyPayloads, PluginPackagePayload[] PackagePayloads, DateTime GeneratedAtUtc, RegistrationItem[] Items, string[] Diagnostics, IReadOnlyDictionary<string, string>? SecureConfigByStepKey = null, IReadOnlyDictionary<string, string>? SecureConfigNameByStepKey = null, string[]? UnmanagedPluginTypes = null, string[]? Info = null, string[]? TypedCustomApiUniqueNames = null, string[]? PluginTypeNames = null);
public sealed record RegistrationDiff(string[] LocalAssemblies, string[] CurrentAssemblies, RegistrationItem[] Added, RegistrationItem[] Removed, RegistrationChange[] Changed, RegistrationItem[] LocalItems, RegistrationItem[] CurrentItems)
{
    public DiffSummary Summary { get; init; } = new DiffSummary(Added.Length, Removed.Length, Changed.Length);
}

public sealed record RegistrationChange(string Kind, string Type, JsonElement Before, JsonElement After);
public sealed record DiffSummary(int Added, int Removed, int Changed);
/// <summary>
/// The plan/apply JSON document (see docs/reference/machine-readable-output.md). <see cref="SchemaVersion"/> is
/// serialized first; it changes only when an existing field changes meaning or shape, never for an added field.
/// <c>diagnostics</c> are warnings (<c>--strict</c> fails on them); <c>info</c> and <c>unmanaged</c> are information.
/// </summary>
public sealed record PlanPreview(string Mode, string? Environment, bool HasChanges, DiffSummary Summary, ApplyOperation[] Operations, RegistrationDiff Diff, string[] Diagnostics, string[] Notes, ExecutedCounts? Executed = null, string[]? Unmanaged = null, string[]? Info = null, ManagedIdentityPlan? ManagedIdentity = null, ApplyFailure? Failure = null)
{
    [System.Text.Json.Serialization.JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = MachineOutput.SchemaVersion;
}

/// <summary>The versioned machine-readable output contract of plan/apply/inspect.</summary>
public static class MachineOutput
{
    /// <summary>Bumped only on a breaking change to an existing field; added fields keep the version.</summary>
    public const int SchemaVersion = 1;
}

/// <summary>
/// What an <c>apply</c> actually DID, taken from the pre-apply diff - distinct from <see cref="PlanPreview.Summary"/>,
/// which after a successful apply is the post-apply remaining diff (all zero). Present only on the <c>apply-execute</c>
/// JSON so a script/agent reads the executed counts directly instead of parsing the free-text notes.
/// </summary>
public sealed record ExecutedCounts(int Created, int Updated, int Deleted, bool PackageRepublished, ExecutedByKind[] ByKind, int AddedToSolution = 0, int LinkedToManagedIdentity = 0)
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

/// <summary>
/// What an <c>apply-failed</c> run did before it stopped: the error, and every planned operation under exactly one of
/// <see cref="Completed"/>, <see cref="Failed"/> and <see cref="NotApplied"/> (the resume report printed on stderr).
/// </summary>
public sealed record ApplyFailure(string Error, string[] Completed, string[] Failed, string[] NotApplied);

/// <summary>The executed create/update/delete counts for one component kind (step / custom-api / web-resource).</summary>
public sealed record ExecutedByKind(string Kind, int Created, int Updated, int Deleted);
public sealed record ApplyOperation(string Action, string Kind, string Type, string Reason);
/// <summary>A plugin package that a plan/apply would write: created when absent from Dataverse, otherwise
/// republished because its content differs. Packages whose deployed content already matches are not reported.
/// Lets <c>plan</c> surface a code-only rebuild (unchanged steps/custom APIs, rebuilt assembly) that the
/// step/custom-API diff alone cannot see.</summary>
public sealed record PackagePublishAction(string PackageName, bool IsCreate);

/// <summary>
/// <c>--managed-identity</c> on plan/apply: the <c>managedidentity</c> record the run links its plugin packages to, and
/// the link state of each package the run publishes (<see cref="ManagedIdentityPackageLink"/>). Absent from the JSON
/// when the option is not given: the link is then neither read nor written.
/// </summary>
public sealed record ManagedIdentityPlan(Guid Id, ManagedIdentityPackageLink[] Packages)
{
    /// <summary>The packages apply writes the link of: not linked yet, or linked to another managed identity.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ManagedIdentityPackageLink[] Pending => Packages.Where(p => p.NeedsWrite).ToArray();
}

/// <summary>
/// One plugin package's <c>managedidentityid</c> compared with <c>--managed-identity</c>. <see cref="Status"/> is
/// <c>linked</c> (already that managed identity), <c>link</c> (no managed identity yet, or the package is created by
/// this apply) or <c>change</c> (linked to <see cref="CurrentManagedIdentityId"/>, another managed identity).
/// </summary>
public sealed record ManagedIdentityPackageLink(string Package, string Status, Guid? CurrentManagedIdentityId = null)
{
    public const string Linked = "linked";
    public const string Link = "link";
    public const string Change = "change";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsWrite => !string.Equals(Status, Linked, StringComparison.Ordinal);
}
public sealed record StepApplyDefinition(string PluginTypeName, string Message, string EntityLogicalName, string StepKey, int Stage, int Mode, int ExecutionOrder, string[] FilteringAttributes, ImageApplyDefinition[] PreImages, ImageApplyDefinition[] PostImages, bool Enabled, string UnsecureConfig, string SecureConfig, Guid ImpersonatingUserId = default, int SupportedDeployment = 0, string Description = "", bool AsyncAutoDelete = false, string? LiveStepKey = null);
// MessagePropertyName: the request property the image is taken from; empty means the message's own.
public sealed record ImageApplyDefinition(string Alias, string[] Attributes, string MessagePropertyName = "");
public sealed record StepImageApplyDefinition(string Alias, string[] Attributes, int ImageType, string MessagePropertyName = "");
public sealed record CustomApiApplyDefinition(string PluginTypeName, string UniqueName, bool IsFunction, bool IsBound, string? BoundEntityLogicalName, string? ExecutePrivilegeName, bool IsPrivate, int AllowedCustomProcessingStepType, int BindingType, bool WorkflowSdkStepEnabled, CustomApiParameterApplyDefinition[] InputParameters, CustomApiParameterApplyDefinition[] OutputParameters, string DisplayName = "", string Description = "");
public sealed record CustomApiParameterApplyDefinition(string Name, bool IsOptional, string? ParameterTypeName, bool IsCollection, string? LogicalEntityName, string UniqueName = "", string Description = "");
