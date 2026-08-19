using System;
using System.Text.Json;

namespace Vela.Cli;

public sealed class DataverseConnectionException : Exception
{
    public DataverseConnectionException(string message) : base(message) { }
    public DataverseConnectionException(string message, Exception innerException) : base(message, innerException) { }
}
public sealed record SharedOptions(string[] PackagePaths, string? PackageAssembly, string? ConnectionString, string? OAuthUrl, string? OAuthToken, string? SolutionUniqueName, string? Environment, string? OutPath, bool Execute, bool Interactive, bool Strict, bool Verbose);
public sealed record ExtractedPackage(string PackagePath, string TempDirectory, string PrimaryAssemblyPath, string[] RegistrableAssemblyPaths);
public sealed record AssemblyPayload(string AssemblyName, string AssemblyPath);
public sealed record PluginPackagePayload(string PackageName, string Version, string PackagePath);
public sealed record RegistrationItem(string Kind, string Type, JsonElement Definition);
public sealed record RegistrationState(string[] SourceAssemblies, AssemblyPayload[] SourceAssemblyPayloads, PluginPackagePayload[] PackagePayloads, DateTime GeneratedAtUtc, RegistrationItem[] Items, string[] Diagnostics);
public sealed record RegistrationDiff(string[] LocalAssemblies, string[] CurrentAssemblies, RegistrationItem[] Added, RegistrationItem[] Removed, RegistrationChange[] Changed, RegistrationItem[] LocalItems, RegistrationItem[] CurrentItems)
{
    public DiffSummary Summary { get; init; } = new DiffSummary(Added.Length, Removed.Length, Changed.Length);
}

public sealed record RegistrationChange(string Kind, string Type, JsonElement Before, JsonElement After);
public sealed record DiffSummary(int Added, int Removed, int Changed);
public sealed record PlanPreview(string Mode, string? Environment, bool HasChanges, DiffSummary Summary, ApplyOperation[] Operations, RegistrationDiff Diff, string[] Diagnostics, string[] Notes);
public sealed record ApplyOperation(string Action, string Kind, string Type, string Reason);
public sealed record StepApplyDefinition(string PluginTypeName, string Message, string EntityLogicalName, string StepKey, string HandlerMethodName, int Stage, int Mode, int ExecutionOrder, string[] FilteringAttributes, ImageApplyDefinition[] PreImages, ImageApplyDefinition[] PostImages, bool Enabled, string UnsecureConfig, string SecureConfig);
public sealed record ImageApplyDefinition(string Alias, string[] Attributes);
public sealed record StepImageApplyDefinition(string Alias, string[] Attributes, int ImageType);
public sealed record CustomApiApplyDefinition(string PluginTypeName, string UniqueName, bool IsFunction, bool IsBound, string? BoundEntityLogicalName, string? ExecutePrivilegeName, bool IsPrivate, int AllowedCustomProcessingStepType, int BindingType, bool WorkflowSdkStepEnabled, CustomApiParameterApplyDefinition[] InputParameters, CustomApiParameterApplyDefinition[] OutputParameters);
public sealed record CustomApiParameterApplyDefinition(string Name, bool IsOptional, string? ParameterTypeName, bool IsCollection, string? LogicalEntityName);
