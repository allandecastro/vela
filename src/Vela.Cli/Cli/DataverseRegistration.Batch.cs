using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>
/// Batched write path for apply: step and Custom API create/update/delete are collected into dependency-ordered
/// tiers and flushed to Dataverse via chunked <c>ExecuteMultiple</c> requests (one round-trip per chunk instead of
/// one per record). A capturing <see cref="IOrganizationService"/> records the writes the existing one-by-one
/// helpers would issue, so all their validation/idempotence logic is reused unchanged; the recorded requests are
/// then executed in tiers (parents before children) with a per-item fault map, per-owner reporting and a
/// one-by-one fallback whenever a whole batch cannot run.
/// </summary>
internal static partial class DataverseRegistration
{
    // Chunk size well under Dataverse's 1000-request ExecuteMultiple cap so one request comfortably fits inside the
    // connection timeout. 100 keeps each round-trip small while still collapsing hundreds of writes into a handful.
    internal const int DefaultBatchChunkSize = 100;

    // ---- Delete phase (reverse dependency order: children first, then parents) ----------------------------------

    private static void ApplyDeletesBatched(
        IOrganizationService service,
        string? solutionUniqueName,
        List<RegistrationItem> removedSteps,
        List<RegistrationItem> removedApis,
        string[] currentAssemblies,
        bool verbose,
        int chunkSize,
        ApplyProgress progress)
    {
        BatchCapturingService capturing = new BatchCapturingService(service, solutionUniqueName);
        List<CapturedOwner> owners = [];
        int seq = 0;

        // A removed step or Custom API is resolved only among the plugin types of the assemblies read for this run (the
        // ones the diff's live side came from), queried once for the whole delete phase rather than per item.
        IReadOnlyDictionary<string, Guid> ourPluginTypeIds = removedSteps.Count == 0 && removedApis.Count == 0
            ? new Dictionary<string, Guid>()
            : GetPluginTypeIdByNameForAssemblies(service, currentAssemblies);

        // Nothing is written to Dataverse during capture; the recorded operations flush only at ExecuteBatchTier
        // below. So no owner is recorded as completed here - a synchronous validation throw mid-capture would
        // otherwise report owners that were in fact never applied (#305). Each owner's outcome is recorded after
        // the flush (RecordOutcomes).
        int planned = progress.Cursor;

        foreach (RegistrationItem item in removedSteps)
        {
            string stepKey = TryGetString(item.Definition, "stepKey") ?? item.Type;
            BatchOwner owner = new BatchOwner($"del-step:{seq}:{stepKey}", $"Plugin {item.Type}", "step", planned,
                $"skipping step delete for '{item.Type}' (managed by custom API)");
            CaptureOwner(capturing, owner, owners, progress, () =>
            {
                LogOperation("step", "delete", stepKey, verbose: verbose);
                DeleteStepByDefinition(capturing, item.Definition, ourPluginTypeIds);
            });

            planned++;
            seq++;
        }

        foreach (RegistrationItem item in removedApis)
        {
            string uniqueName = TryGetString(item.Definition, "uniqueName") ?? item.Type;
            BatchOwner owner = new BatchOwner($"del-api:{seq}:{uniqueName}", uniqueName, "custom-api", planned,
                $"skipping custom API delete for '{item.Type}'");
            CaptureOwner(capturing, owner, owners, progress, () =>
            {
                LogOperation("custom-api", "delete", uniqueName, verbose: verbose);
                DeleteCustomApi(capturing, item.Definition, ourPluginTypeIds);
            });

            planned++;
            seq++;
        }

        BatchApplyReport report = new BatchApplyReport();
        if (capturing.Operations.Count > 0)
        {
            HashSet<string> skip = new HashSet<string>();
            // Children first, then the step/API rows, then a step's secure-config row. The secure config goes last, in
            // its own tier, so it is deleted only once its step's delete succeeded: an owner whose step delete failed or
            // was ignored is in `skip`, and its secure config is kept for the step that survives.
            List<CapturedOperation> children = capturing.Operations.Where(IsChildDelete).ToList();
            List<CapturedOperation> secureConfigs = capturing.Operations.Where(o => o.Category == "secure-config").ToList();
            List<CapturedOperation> parents = capturing.Operations.Where(o => !IsChildDelete(o) && o.Category != "secure-config").ToList();
            // DeleteCustomApi checked the API's owner during capture, but its deletes run only now: re-check right before
            // the first destructive tier, so an API taken over or detached in between is not deleted.
            parents.RemoveAll(op => !CustomApiDeleteStillOwned(service, op, ourPluginTypeIds, report, skip));
            ExecuteBatchTier(service, children, chunkSize, report, skip);
            ExecuteBatchTier(service, parents, chunkSize, report, skip);
            ExecuteBatchTier(service, secureConfigs, chunkSize, report, skip);
            RenderBatchReport(report);
        }

        RecordOutcomes(owners, report, progress, planned);
    }

    // True unless op is the delete of a Custom API that is no longer implemented by a plugin type of the assemblies
    // being applied. Such an owner is recorded as failed and skipped, so neither its parameters nor the API are deleted.
    // An API already gone has nothing to protect: its deletes are idempotent.
    private static bool CustomApiDeleteStillOwned(
        IOrganizationService service,
        CapturedOperation op,
        IReadOnlyDictionary<string, Guid> ourPluginTypeIds,
        BatchApplyReport report,
        HashSet<string> skip)
    {
        if (!op.IsDelete || op.Category != "custom-api" || op.Request is not DeleteRequest delete)
        {
            return true;
        }

        EntityReference? implementation;
        try
        {
            implementation = service.Retrieve("customapi", delete.Target.Id, new ColumnSet("plugintypeid"))?.GetAttributeValue<EntityReference>("plugintypeid");
        }
        catch (Exception ex) when (ExtractFault(ex).ErrorCode == ObjectDoesNotExistErrorCode)
        {
            return true;
        }

        if (implementation != null && ourPluginTypeIds.Values.Contains(implementation.Id))
        {
            return true;
        }

        report.RecordFailure(op, new OrganizationServiceFault { Message = CustomApiNoLongerOursMessage(op.Owner.ReportGroup) });
        skip.Add(op.Owner.Id);
        return false;
    }

    private static bool IsChildDelete(CapturedOperation op)
        => op.Category is "request-parameter" or "response-property" or "image";

    // ---- Upsert phases (Custom APIs, then steps; tier 1: rows, a created step's disable; tier 2: children) -------

    private static void ApplyUpsertsBatched(
        IOrganizationService service,
        string? solutionUniqueName,
        Dictionary<string, Guid> pluginTypeIds,
        List<RegistrationItem> addedApis,
        List<RegistrationChange> changedApis,
        List<RegistrationItem> addedSteps,
        List<RegistrationChange> changedSteps,
        string? customApiSolutionPrefix,
        IReadOnlyDictionary<string, string>? secureConfigByStepKey,
        StepMetadataCache metadataCache,
        bool verbose,
        int chunkSize,
        ApplyProgress progress)
    {
        // Nothing is written during capture (see ApplyDeletesBatched): outcomes are recorded only after the flush.
        int planned = progress.Cursor;

        // Two phases, each captured then flushed: the Custom APIs (with their parameters and response properties), then
        // the steps. Capturing a step resolves its sdkmessage from the live environment, and the message of a Custom
        // API is created by Dataverse with the Custom API: a step on an API this run creates can only be captured once
        // that API is written. A failed Custom API phase stops the run before the steps (RecordOutcomes throws), so a
        // step is never attempted on an API that was not written.
        BatchCapturingService apis = new BatchCapturingService(service, solutionUniqueName);
        List<CapturedOwner> apiOwners = [];
        foreach (RegistrationItem item in addedApis)
        {
            string uniqueName = TryGetString(item.Definition, "uniqueName") ?? item.Type;
            CaptureOwner(apis, new BatchOwner($"api-add:{planned}:{uniqueName}", uniqueName, "custom-api", planned,
                $"skipping custom API upsert for '{item.Type}'"), apiOwners, progress,
                () => UpsertCustomApi(apis, pluginTypeIds, item.Definition, verbose, customApiSolutionPrefix));
            planned++;
        }

        foreach (RegistrationChange change in changedApis)
        {
            string uniqueName = TryGetString(change.After, "uniqueName") ?? change.Type;
            CaptureOwner(apis, new BatchOwner($"api-chg:{planned}:{uniqueName}", uniqueName, "custom-api", planned,
                $"skipping custom API update for '{change.Type}'"), apiOwners, progress,
                () => UpsertCustomApi(apis, pluginTypeIds, change.After, verbose, customApiSolutionPrefix));
            planned++;
        }

        FlushUpserts(service, apis, apiOwners, chunkSize, progress, planned);

        BatchCapturingService steps = new BatchCapturingService(service, solutionUniqueName);
        List<CapturedOwner> stepOwners = [];
        foreach (RegistrationItem item in addedSteps)
        {
            CaptureOwner(steps, new BatchOwner($"step:{planned}", $"Plugin {item.Type}", "step", planned, ignorableWarning: null), stepOwners, progress,
                () => UpsertStep(steps, pluginTypeIds, item.Definition, verbose, metadataCache, secureConfigByStepKey));
            planned++;
        }

        foreach (RegistrationChange change in changedSteps)
        {
            CaptureOwner(steps, new BatchOwner($"step:{planned}", $"Plugin {change.Type}", "step", planned, ignorableWarning: null), stepOwners, progress,
                () => UpsertStep(steps, pluginTypeIds, change.After, verbose, metadataCache, secureConfigByStepKey));
            planned++;
        }

        FlushUpserts(service, steps, stepOwners, chunkSize, progress, planned);
    }

    // Flushes one captured upsert phase in two tiers and records each owner's outcome (throws on any failure).
    private static void FlushUpserts(IOrganizationService service, BatchCapturingService capturing, List<CapturedOwner> owners, int chunkSize, ApplyProgress progress, int phaseEnd)
    {
        BatchApplyReport report = new BatchApplyReport();
        if (capturing.Operations.Count > 0)
        {
            HashSet<string> skip = new HashSet<string>();
            // Tier 1 is the row itself (the first write captured for each owner); tier 2 (images, parameters, secure
            // config) references the row's id, so it runs only after tier 1 and is skipped for any owner whose
            // tier-1 row failed.
            //
            // Exception: the Update that disables a step just created runs in tier 1, right behind its create.
            // Dataverse ignores statecode on Create, so a .Disabled() step is born enabled (UpsertStep); deferring the
            // disable to tier 2 would let it fire on live traffic for the whole of tier 1, and leave it enabled if the
            // run stopped between the tiers. ExecuteMultiple runs its requests in order, so it follows its create.
            static bool InTier1(CapturedOperation o) => o.SeqInOwner == 0 || o.Category == "state";
            List<CapturedOperation> tier1 = capturing.Operations.Where(InTier1).ToList();
            List<CapturedOperation> tier2 = capturing.Operations.Where(o => !InTier1(o)).ToList();
            ExecuteBatchTier(service, tier1, chunkSize, report, skip);
            ExecuteBatchTier(service, tier2, chunkSize, report, skip);
            RenderBatchReport(report);
        }

        RecordOutcomes(owners, report, progress, phaseEnd);
    }

    // One owner captured in a batched phase, with the progress lines it logged during capture.
    private sealed record CapturedOwner(BatchOwner Owner, List<string> Log);

    // Captures one owner's writes. The progress lines the helpers log (LogOperation) are buffered rather than printed:
    // nothing has run yet, so "[step] create X" would claim a write that may never land. RecordOutcomes prints them
    // once the owner's outcome is known. An ignorable platform-stage fault is warned and skipped; any other throw
    // fails this owner (and the run) before anything of the phase was flushed.
    private static void CaptureOwner(BatchCapturingService capturing, BatchOwner owner, List<CapturedOwner> owners, ApplyProgress progress, Action capture)
    {
        CapturedOwner captured = new(owner, []);
        owners.Add(captured);
        capturing.BeginOwner(owner);
        Action<string>? previous = LogSink;
        LogSink = captured.Log.Add;
        try
        {
            capture();
        }
        catch (Exception ex) when (owner.IgnorableWarning != null && IsIgnorableCustomApiStageException(ex))
        {
            Console.Error.WriteLine($"Warning: {owner.IgnorableWarning}: {OutputWriter.RedactSecrets(ex.Message)}");
        }
        catch
        {
            progress.Fail(owner.PlannedIndex);
            throw;
        }
        finally
        {
            LogSink = previous;
        }
    }

    // Records every owner of a flushed phase as completed or failed (a continue-on-error batch can fail an early owner
    // and still apply later ones), prints the buffered progress lines with their real outcome, moves the cursor past
    // the phase and, if any owner failed, throws the earliest failure for the resume report.
    private static void RecordOutcomes(List<CapturedOwner> owners, BatchApplyReport report, ApplyProgress progress, int phaseEnd)
    {
        HashSet<string> failedOwners = new(report.Failures.Select(f => f.Op.Owner.Id), StringComparer.Ordinal);
        foreach (CapturedOwner captured in owners)
        {
            bool failed = failedOwners.Contains(captured.Owner.Id);
            if (failed)
            {
                progress.Fail(captured.Owner.PlannedIndex);
            }
            else
            {
                progress.Complete(captured.Owner.PlannedIndex);
            }

            foreach (string line in captured.Log)
            {
                Console.Error.WriteLine(failed ? $"{line} (not applied)" : line);
            }
        }

        progress.MoveTo(phaseEnd);
        ThrowIfBatchFailed(report);
    }

    // ---- Executor -----------------------------------------------------------------------------------------------

    // Executes one dependency tier in chunks. Each chunk is issued as a single ExecuteMultiple with
    // ContinueOnError/ReturnResponses so a per-item fault does not abort the rest; if the whole request cannot run
    // (not supported, timeout, malformed response) the chunk falls back to one-by-one execution so an environment
    // quirk can never break apply. Owners that already failed a prior tier are skipped, not re-issued.
    internal static void ExecuteBatchTier(
        IOrganizationService service,
        List<CapturedOperation> ops,
        int chunkSize,
        BatchApplyReport report,
        HashSet<string> skipOwners)
    {
        foreach (List<CapturedOperation> chunk in BuildBatchChunks(ops, chunkSize))
        {
            List<CapturedOperation> live = new List<CapturedOperation>();
            foreach (CapturedOperation op in chunk)
            {
                if (skipOwners.Contains(op.Owner.Id))
                {
                    report.RecordSkipped(op);
                }
                else
                {
                    live.Add(op);
                }
            }

            if (live.Count == 0)
            {
                continue;
            }

            ExecuteMultipleResponse? response = null;
            string? wholeBatchError = null;
            try
            {
                ExecuteMultipleRequest request = new ExecuteMultipleRequest
                {
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true },
                    Requests = new OrganizationRequestCollection(),
                };
                foreach (CapturedOperation op in live)
                {
                    request.Requests.Add(op.Request);
                }

                report.BatchRequestCount++;
                response = service.Execute(request) as ExecuteMultipleResponse;
                if (response == null || !response.Results.ContainsKey("Responses"))
                {
                    wholeBatchError = "ExecuteMultiple returned no responses";
                }
            }
            catch (Exception ex)
            {
                // ExecuteMultiple can throw as a whole (not-supported / timeout / connection). Fall back below, but keep
                // the cause: when the replay fails too, it is often the real reason (an expired token, a timeout).
                wholeBatchError = ex.Message;
            }

            if (wholeBatchError != null)
            {
                FallbackOneByOne(service, live, report, skipOwners, wholeBatchError);
                continue;
            }

            Dictionary<int, ExecuteMultipleResponseItem> byIndex = new Dictionary<int, ExecuteMultipleResponseItem>();
            foreach (ExecuteMultipleResponseItem item in response!.Responses)
            {
                byIndex[item.RequestIndex] = item;
            }

            for (int i = 0; i < live.Count; i++)
            {
                if (!byIndex.TryGetValue(i, out ExecuteMultipleResponseItem? item))
                {
                    // With ReturnResponses every request gets an item (a response or a fault). One without an item has
                    // no known outcome: reporting it as applied would hide a write that may never have landed.
                    HandleBatchFault(live[i], new OrganizationServiceFault { Message = $"ExecuteMultiple returned no response for request {i}; its outcome is unknown." }, report, skipOwners);
                }
                else if (item.Fault != null)
                {
                    HandleBatchFault(live[i], item.Fault, report, skipOwners);
                }
                else
                {
                    report.RecordOk(live[i]);
                }
            }
        }
    }

    // Slices a tier into ExecuteMultiple chunks of at most chunkSize requests, except that a created step's disable (a
    // "state" op) always shares its create's chunk: split across two calls, a crash between them would leave the new
    // step enabled on live traffic (#445). Such a pair opens a fresh chunk when it does not fit the current one, and
    // with chunkSize 1 it forms a chunk of two, well under the 1000-request cap.
    internal static List<List<CapturedOperation>> BuildBatchChunks(List<CapturedOperation> ops, int chunkSize)
    {
        if (chunkSize < 1)
        {
            chunkSize = 1;
        }

        List<List<CapturedOperation>> chunks = new List<List<CapturedOperation>>();
        List<CapturedOperation> current = new List<CapturedOperation>();
        for (int i = 0; i < ops.Count; i++)
        {
            int unitSize = i + 1 < ops.Count && IsStateOf(ops[i + 1], ops[i]) ? 2 : 1;
            if (current.Count > 0 && current.Count + unitSize > chunkSize)
            {
                chunks.Add(current);
                current = new List<CapturedOperation>();
            }

            current.AddRange(ops.Skip(i).Take(unitSize));
            i += unitSize - 1;
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    private static bool IsStateOf(CapturedOperation state, CapturedOperation row)
        => state.Category == "state" && ReferenceEquals(state.Owner, row.Owner);

    private static void FallbackOneByOne(
        IOrganizationService service,
        List<CapturedOperation> ops,
        BatchApplyReport report,
        HashSet<string> skipOwners,
        string batchError)
    {
        foreach (CapturedOperation op in ops)
        {
            if (skipOwners.Contains(op.Owner.Id))
            {
                report.RecordSkipped(op);
                continue;
            }

            try
            {
                service.Execute(op.Request);
                report.RecordOk(op);
            }
            catch (Exception ex)
            {
                // The replay's own fault can hide the root cause (an expired token, a timeout), so the whole-batch
                // failure is carried into the message the report and the resume summary show.
                OrganizationServiceFault fault = ExtractFault(ex);
                fault.Message = $"{fault.Message} (replayed one by one after the batch request failed: {batchError})";
                HandleBatchFault(op, fault, report, skipOwners, replayed: true);
            }
        }
    }

    // Dataverse "Cannot insert duplicate key" (0x80040237): a create whose id already exists.
    internal const int DuplicateRecordErrorCode = unchecked((int)0x80040237);

    private static void HandleBatchFault(
        CapturedOperation op,
        OrganizationServiceFault fault,
        BatchApplyReport report,
        HashSet<string> skipOwners,
        bool replayed = false)
    {
        // A delete of an already-absent record is a success: apply stays idempotent on re-run.
        if (op.IsDelete && fault.ErrorCode == ObjectDoesNotExistErrorCode)
        {
            report.RecordOk(op);
            return;
        }

        // A create replayed by the one-by-one fallback whose client-assigned id already exists: the whole-batch call
        // threw (a timeout, a dropped connection) after the server had run it, so this row already landed. Counting
        // it as a failure would report a write that succeeded, and abort the owner's dependent writes.
        if (replayed && op.HasClientAssignedId && fault.ErrorCode == DuplicateRecordErrorCode)
        {
            report.RecordOk(op);
            return;
        }

        bool ignorable = fault.ErrorCode == InvalidPluginRegistrationStageErrorCode
            || (fault.Message ?? string.Empty).Contains("Invalid plug-in registration stage", StringComparison.OrdinalIgnoreCase);
        if (ignorable && op.Owner.IgnorableWarning != null)
        {
            Console.Error.WriteLine($"Warning: {op.Owner.IgnorableWarning}: {OutputWriter.RedactSecrets(fault.Message)}");
            report.RecordIgnored(op);
            skipOwners.Add(op.Owner.Id);
            return;
        }

        report.RecordFailure(op, fault);
        // A failed row means its dependent tier-2 writes cannot land: skip them and report as skipped.
        skipOwners.Add(op.Owner.Id);
    }

    private static OrganizationServiceFault ExtractFault(Exception ex)
    {
        Exception? current = ex;
        while (current != null)
        {
            if (current is FaultException<OrganizationServiceFault> fault)
            {
                OrganizationServiceFault detail = fault.Detail ?? new OrganizationServiceFault();
                return new OrganizationServiceFault
                {
                    ErrorCode = detail.ErrorCode,
                    Message = string.IsNullOrEmpty(detail.Message) ? fault.Message : detail.Message,
                };
            }

            current = current.InnerException;
        }

        return new OrganizationServiceFault { Message = ex.Message };
    }

    private static void RenderBatchReport(BatchApplyReport report)
    {
        // A half-applied state (any owner failed, or was skipped because a prior tier failed) is always surfaced,
        // even without --verbose: the operator must see which rows landed and which did not. A fully-successful apply
        // (nothing failed or skipped) stays silent. (#305)
        if (report.Failed == 0 && report.Skipped == 0)
        {
            return;
        }

        foreach ((string group, Dictionary<string, int[]> categories) in report.ByGroup)
        {
            if (!categories.Values.Any(c => c[1] > 0 || c[2] > 0))
            {
                continue;
            }

            List<string> parts = new List<string>();
            foreach ((string category, int[] counts) in categories)
            {
                if (counts[0] > 0)
                {
                    parts.Add($"{counts[0]} {category} ok");
                }

                if (counts[1] > 0)
                {
                    parts.Add($"{counts[1]} {category} FAILED");
                }

                if (counts[2] > 0)
                {
                    parts.Add($"{counts[2]} {category} skipped");
                }

                if (counts[3] > 0)
                {
                    parts.Add($"{counts[3]} {category} ignored");
                }
            }

            Console.Error.WriteLine($"[apply] {group}: {string.Join(", ", parts)}");
        }

        foreach ((CapturedOperation op, OrganizationServiceFault fault) in report.Failures)
        {
            Console.Error.WriteLine($"  - {op.Owner.ReportGroup} {op.Category}: {OutputWriter.RedactSecrets(fault.Message)} (code {fault.ErrorCode})");
        }
    }

    // On any batched failure, rethrow the earliest-planned failure so apply exits through the resume report (every
    // owner's outcome is already recorded in the progress). Already-applied writes are idempotent, so a re-run
    // reconciles the rest.
    private static void ThrowIfBatchFailed(BatchApplyReport report)
    {
        if (report.Failures.Count == 0)
        {
            return;
        }

        (CapturedOperation _, OrganizationServiceFault fault) = report.Failures
            .OrderBy(f => f.Op.Owner.PlannedIndex)
            .First();
        throw new InvalidOperationException(
            string.IsNullOrEmpty(fault.Message) ? "batched apply operation failed" : fault.Message);
    }
}

/// <summary>
/// One write recorded by <see cref="BatchCapturingService"/>: the SDK request to issue, the owner it belongs to,
/// its position within that owner's writes (0 = the row itself) and a category used for tier assignment/reporting.
/// </summary>
internal sealed class CapturedOperation
{
    public CapturedOperation(OrganizationRequest request, BatchOwner owner, int seqInOwner, string category, bool isDelete, bool hasClientAssignedId = false)
    {
        Request = request;
        Owner = owner;
        SeqInOwner = seqInOwner;
        Category = category;
        IsDelete = isDelete;
        HasClientAssignedId = hasClientAssignedId;
    }

    public OrganizationRequest Request { get; }
    public BatchOwner Owner { get; }
    public int SeqInOwner { get; }
    public string Category { get; }
    public bool IsDelete { get; }

    /// <summary> A create whose id the capturing service assigned (a fresh GUID), so a duplicate can only be itself. </summary>
    public bool HasClientAssignedId { get; }
}

/// <summary> The plugin step or Custom API a batch of writes belongs to, for correlation and reporting. </summary>
internal sealed class BatchOwner
{
    public BatchOwner(string id, string reportGroup, string kind, int plannedIndex, string? ignorableWarning)
    {
        Id = id;
        ReportGroup = reportGroup;
        Kind = kind;
        PlannedIndex = plannedIndex;
        IgnorableWarning = ignorableWarning;
    }

    /// <summary> Unique correlation key: dependent writes and skip-on-failure key off this. </summary>
    public string Id { get; }

    /// <summary> Human grouping for reporting (e.g. "Plugin Foo" or a Custom API unique name). </summary>
    public string ReportGroup { get; }

    /// <summary> "step" or "custom-api". </summary>
    public string Kind { get; }

    /// <summary> Index of this owner's operation in the run's planned list (drives the resume report). </summary>
    public int PlannedIndex { get; }

    /// <summary> Warning text to print when this owner hits an ignorable platform-stage fault, or null. </summary>
    public string? IgnorableWarning { get; }
}

/// <summary> Per-owner/per-category aggregation of a batched apply's outcomes. </summary>
internal sealed class BatchApplyReport
{
    public int Ok { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public int Ignored { get; private set; }
    public int BatchRequestCount { get; set; }

    public List<(CapturedOperation Op, OrganizationServiceFault Fault)> Failures { get; } = new();

    // group -> category -> [ok, failed, skipped]
    public Dictionary<string, Dictionary<string, int[]>> ByGroup { get; } = new();

    public void RecordOk(CapturedOperation op)
    {
        Ok++;
        Bump(op, 0);
    }

    public void RecordFailure(CapturedOperation op, OrganizationServiceFault fault)
    {
        Failed++;
        Bump(op, 1);
        Failures.Add((op, fault));
    }

    public void RecordSkipped(CapturedOperation op)
    {
        Skipped++;
        Bump(op, 2);
    }

    public void RecordIgnored(CapturedOperation op)
    {
        Ignored++;
        Bump(op, 3);
    }

    private void Bump(CapturedOperation op, int slot)
    {
        if (!ByGroup.TryGetValue(op.Owner.ReportGroup, out Dictionary<string, int[]>? categories))
        {
            categories = new Dictionary<string, int[]>();
            ByGroup[op.Owner.ReportGroup] = categories;
        }

        if (!categories.TryGetValue(op.Category, out int[]? counts))
        {
            counts = new int[4]; // [ok, failed, skipped, ignored]
            categories[op.Category] = counts;
        }

        counts[slot]++;
    }
}

/// <summary>
/// An <see cref="IOrganizationService"/> decorator that records every Create/Update/Delete the existing
/// registration helpers would issue instead of executing it, so their unchanged logic produces a batchable plan.
/// Reads pass through to the real service; a read of a row this session captured as a create (a not-yet-flushed
/// parent) is answered locally as an empty/absent record so child reconciliation behaves as it would against a
/// freshly-created row. Creates get a client-assigned id so dependent writes can reference the parent before flush.
/// </summary>
internal sealed class BatchCapturingService : IOrganizationService
{
    private readonly IOrganizationService _inner;
    private readonly string? _solutionUniqueName;
    private readonly HashSet<Guid> _capturedCreatedIds = new();
    private readonly Dictionary<string, int> _seqByOwner = new(StringComparer.Ordinal);
    private BatchOwner? _owner;

    public BatchCapturingService(IOrganizationService inner, string? solutionUniqueName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _solutionUniqueName = string.IsNullOrWhiteSpace(solutionUniqueName) ? null : solutionUniqueName;
    }

    public List<CapturedOperation> Operations { get; } = new();

    public void BeginOwner(BatchOwner owner) => _owner = owner;

    public Guid Create(Entity entity)
    {
        bool clientAssigned = entity.Id == Guid.Empty;
        Guid id = clientAssigned ? Guid.NewGuid() : entity.Id;
        entity.Id = id;
        _capturedCreatedIds.Add(id);
        CreateRequest request = new CreateRequest { Target = entity };
        ApplySolution(request);
        Capture(request, entity.LogicalName, "create", isDelete: false, clientAssigned);
        return id;
    }

    public void Update(Entity entity)
    {
        UpdateRequest request = new UpdateRequest { Target = entity };
        ApplySolution(request);
        // A step update that carries only the state is the disable of a step just created (UpsertStep).
        Capture(request, entity.LogicalName, IsStateOnly(entity) ? "state" : "update", isDelete: false);
    }

    private static bool IsStateOnly(Entity entity)
        => entity.Attributes.Count == 2 && entity.Contains("statecode") && entity.Contains("statuscode");

    public void Delete(string entityName, Guid id)
    {
        DeleteRequest request = new DeleteRequest { Target = new EntityReference(entityName, id) };
        Capture(request, entityName, "delete", isDelete: true);
    }

    public OrganizationResponse Execute(OrganizationRequest request) => _inner.Execute(request);

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        => _capturedCreatedIds.Contains(id) ? new Entity(entityName, id) : _inner.Retrieve(entityName, id, columnSet);

    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        if (query is QueryExpression expression && ReferencesCapturedId(expression))
        {
            return new EntityCollection();
        }

        return _inner.RetrieveMultiple(query);
    }

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        => _inner.Associate(entityName, entityId, relationship, relatedEntities);

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        => _inner.Disassociate(entityName, entityId, relationship, relatedEntities);

    private void ApplySolution(OrganizationRequest request)
    {
        if (_solutionUniqueName != null)
        {
            request["SolutionUniqueName"] = _solutionUniqueName;
        }
    }

    private void Capture(OrganizationRequest request, string targetEntity, string verb, bool isDelete, bool hasClientAssignedId = false)
    {
        BatchOwner owner = _owner ?? throw new InvalidOperationException("BatchCapturingService: no owner set before a write.");
        int seq = _seqByOwner.TryGetValue(owner.Id, out int current) ? current : 0;
        _seqByOwner[owner.Id] = seq + 1;
        Operations.Add(new CapturedOperation(request, owner, seq, Categorize(targetEntity, verb, seq, owner.Kind), isDelete, hasClientAssignedId));
    }

    private static string Categorize(string targetEntity, string verb, int seq, string ownerKind)
    {
        if (verb == "state")
        {
            return "state";
        }

        return targetEntity switch
        {
            "customapi" => "custom-api",
            "customapirequestparameter" => "request-parameter",
            "customapiresponseproperty" => "response-property",
            "sdkmessageprocessingstepimage" => "image",
            "sdkmessageprocessingstepsecureconfig" => "secure-config",
            // The first write for a step owner is the step row (tier 1); a later write to the same table is the
            // secure-config link update (tier 2).
            "sdkmessageprocessingstep" => ownerKind == "step" && seq == 0 ? "step" : "secure-config",
            _ => targetEntity,
        };
    }

    private bool ReferencesCapturedId(QueryExpression expression)
        => FilterReferencesCapturedId(expression.Criteria)
           || expression.LinkEntities.Any(LinkReferencesCapturedId);

    private bool FilterReferencesCapturedId(FilterExpression filter)
        => filter.Conditions.Any(c => c.Values.Any(v => v is Guid g && _capturedCreatedIds.Contains(g)))
           || filter.Filters.Any(FilterReferencesCapturedId);

    private bool LinkReferencesCapturedId(LinkEntity link)
        => FilterReferencesCapturedId(link.LinkCriteria)
           || link.LinkEntities.Any(LinkReferencesCapturedId);
}
