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
        bool verbose,
        int chunkSize,
        ref int done)
    {
        BatchCapturingService capturing = new BatchCapturingService(service, solutionUniqueName);
        int seq = 0;

        // Nothing is written to Dataverse during capture; the recorded operations flush only at ExecuteBatchTier
        // below. So the resume boundary (done) must not advance here - a synchronous validation throw mid-capture
        // would otherwise report already-"completed" owners that were in fact never applied (#305). Track the planned
        // index locally and commit it to done only after a successful flush; on a batched failure ThrowIfBatchFailed
        // sets done to the earliest failed owner instead.
        int planned = done;

        foreach (RegistrationItem item in removedSteps)
        {
            string stepKey = TryGetString(item.Definition, "stepKey") ?? item.Type;
            BatchOwner owner = new BatchOwner($"del-step:{seq}:{stepKey}", $"Plugin {item.Type}", "step", planned,
                $"skipping step delete for '{item.Type}' (managed by custom API)");
            try
            {
                LogOperation("step", "delete", stepKey, verbose: verbose);
                capturing.BeginOwner(owner);
                DeleteStepByDefinition(capturing, item.Definition);
            }
            catch (Exception ex) when (IsIgnorableCustomApiStageException(ex))
            {
                Console.Error.WriteLine($"Warning: skipping step delete for '{item.Type}' (managed by custom API): {ex.Message}");
            }

            planned++;
            seq++;
        }

        foreach (RegistrationItem item in removedApis)
        {
            string uniqueName = TryGetString(item.Definition, "uniqueName") ?? item.Type;
            BatchOwner owner = new BatchOwner($"del-api:{seq}:{uniqueName}", uniqueName, "custom-api", planned,
                $"skipping custom API delete for '{item.Type}'");
            try
            {
                LogOperation("custom-api", "delete", uniqueName, verbose: verbose);
                capturing.BeginOwner(owner);
                DeleteCustomApi(capturing, item.Definition);
            }
            catch (Exception ex) when (IsIgnorableCustomApiStageException(ex))
            {
                Console.Error.WriteLine($"Warning: skipping custom API delete for '{item.Type}': {ex.Message}");
            }

            planned++;
            seq++;
        }

        if (capturing.Operations.Count == 0)
        {
            done = planned;
            return;
        }

        BatchApplyReport report = new BatchApplyReport();
        HashSet<string> skip = new HashSet<string>();
        // Children first, then the step/API rows, then a step's secure-config row. The secure config goes last, in its
        // own tier, so it is deleted only once its step's delete succeeded: an owner whose step delete failed or was
        // ignored is in `skip`, and its secure config is kept for the step that survives.
        List<CapturedOperation> children = capturing.Operations.Where(IsChildDelete).ToList();
        List<CapturedOperation> secureConfigs = capturing.Operations.Where(o => o.Category == "secure-config").ToList();
        List<CapturedOperation> parents = capturing.Operations.Where(o => !IsChildDelete(o) && o.Category != "secure-config").ToList();
        ExecuteBatchTier(service, children, chunkSize, report, skip);
        ExecuteBatchTier(service, parents, chunkSize, report, skip);
        ExecuteBatchTier(service, secureConfigs, chunkSize, report, skip);
        RenderBatchReport(report);
        ThrowIfBatchFailed(report, ref done);
        done = planned;
    }

    private static bool IsChildDelete(CapturedOperation op)
        => op.Category is "request-parameter" or "response-property" or "image";

    // ---- Upsert phase (tier 1: step/api rows; tier 2: images, params, secure config, state) ---------------------

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
        ref int done)
    {
        BatchCapturingService capturing = new BatchCapturingService(service, solutionUniqueName);

        // Nothing is written during capture (see ApplyDeletesBatched): the resume boundary must not advance until a
        // successful flush, or a synchronous validation throw mid-capture would over-report earlier owners as
        // completed while nothing was applied (#305). Track the planned index locally; commit to done after the flush.
        int planned = done;

        foreach (RegistrationItem item in addedApis)
        {
            string uniqueName = TryGetString(item.Definition, "uniqueName") ?? item.Type;
            capturing.BeginOwner(new BatchOwner($"api-add:{planned}:{uniqueName}", uniqueName, "custom-api", planned,
                $"skipping custom API upsert for '{item.Type}'"));
            try
            {
                UpsertCustomApi(capturing, pluginTypeIds, item.Definition, verbose, customApiSolutionPrefix);
            }
            catch (Exception ex) when (IsIgnorableCustomApiStageException(ex))
            {
                Console.Error.WriteLine($"Warning: skipping custom API upsert for '{item.Type}': {ex.Message}");
            }

            planned++;
        }

        foreach (RegistrationChange change in changedApis)
        {
            string uniqueName = TryGetString(change.After, "uniqueName") ?? change.Type;
            capturing.BeginOwner(new BatchOwner($"api-chg:{planned}:{uniqueName}", uniqueName, "custom-api", planned,
                $"skipping custom API update for '{change.Type}'"));
            try
            {
                UpsertCustomApi(capturing, pluginTypeIds, change.After, verbose, customApiSolutionPrefix);
            }
            catch (Exception ex) when (IsIgnorableCustomApiStageException(ex))
            {
                Console.Error.WriteLine($"Warning: skipping custom API update for '{change.Type}': {ex.Message}");
            }

            planned++;
        }

        foreach (RegistrationItem item in addedSteps)
        {
            capturing.BeginOwner(new BatchOwner($"step:{planned}", $"Plugin {item.Type}", "step", planned, ignorableWarning: null));
            UpsertStep(capturing, pluginTypeIds, item.Definition, verbose, metadataCache, secureConfigByStepKey);
            planned++;
        }

        foreach (RegistrationChange change in changedSteps)
        {
            capturing.BeginOwner(new BatchOwner($"step:{planned}", $"Plugin {change.Type}", "step", planned, ignorableWarning: null));
            UpsertStep(capturing, pluginTypeIds, change.After, verbose, metadataCache, secureConfigByStepKey);
            planned++;
        }

        if (capturing.Operations.Count == 0)
        {
            done = planned;
            return;
        }

        BatchApplyReport report = new BatchApplyReport();
        HashSet<string> skip = new HashSet<string>();
        // Tier 1 is the row itself (the first write captured for each owner); tier 2 (images, parameters, secure
        // config, state) references the row's id, so it runs only after tier 1 and is skipped for any owner whose
        // tier-1 row failed.
        List<CapturedOperation> tier1 = capturing.Operations.Where(o => o.SeqInOwner == 0).ToList();
        List<CapturedOperation> tier2 = capturing.Operations.Where(o => o.SeqInOwner > 0).ToList();
        ExecuteBatchTier(service, tier1, chunkSize, report, skip);
        ExecuteBatchTier(service, tier2, chunkSize, report, skip);
        RenderBatchReport(report);
        ThrowIfBatchFailed(report, ref done);
        done = planned;
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
        if (chunkSize < 1)
        {
            chunkSize = 1;
        }

        for (int start = 0; start < ops.Count; start += chunkSize)
        {
            List<CapturedOperation> live = new List<CapturedOperation>();
            foreach (CapturedOperation op in ops.Skip(start).Take(chunkSize))
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
            bool wholeBatchFailed = false;
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
                    wholeBatchFailed = true;
                }
            }
            catch
            {
                // ExecuteMultiple can throw as a whole (not-supported / timeout / connection). Fall back below.
                wholeBatchFailed = true;
            }

            if (wholeBatchFailed)
            {
                FallbackOneByOne(service, live, report, skipOwners);
                continue;
            }

            Dictionary<int, ExecuteMultipleResponseItem> byIndex = new Dictionary<int, ExecuteMultipleResponseItem>();
            foreach (ExecuteMultipleResponseItem item in response!.Responses)
            {
                byIndex[item.RequestIndex] = item;
            }

            for (int i = 0; i < live.Count; i++)
            {
                if (byIndex.TryGetValue(i, out ExecuteMultipleResponseItem? item) && item.Fault != null)
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

    private static void FallbackOneByOne(
        IOrganizationService service,
        List<CapturedOperation> ops,
        BatchApplyReport report,
        HashSet<string> skipOwners)
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
                HandleBatchFault(op, ExtractFault(ex), report, skipOwners);
            }
        }
    }

    private static void HandleBatchFault(
        CapturedOperation op,
        OrganizationServiceFault fault,
        BatchApplyReport report,
        HashSet<string> skipOwners)
    {
        // A delete of an already-absent record is a success: apply stays idempotent on re-run.
        if (op.IsDelete && fault.ErrorCode == ObjectDoesNotExistErrorCode)
        {
            report.RecordOk(op);
            return;
        }

        bool ignorable = fault.ErrorCode == InvalidPluginRegistrationStageErrorCode
            || (fault.Message ?? string.Empty).Contains("Invalid plug-in registration stage", StringComparison.OrdinalIgnoreCase);
        if (ignorable && op.Owner.IgnorableWarning != null)
        {
            Console.Error.WriteLine($"Warning: {op.Owner.IgnorableWarning}: {fault.Message}");
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
            Console.Error.WriteLine($"  - {op.Owner.ReportGroup} {op.Category}: {fault.Message} (code {fault.ErrorCode})");
        }
    }

    // On any batched failure, surface the earliest-planned failed operation through the same resume-report path used
    // by synchronous failures: set the completed/pending boundary to that operation and rethrow so apply exits with
    // an accurate report. Already-applied writes are idempotent, so a re-run reconciles the rest.
    private static void ThrowIfBatchFailed(BatchApplyReport report, ref int done)
    {
        if (report.Failures.Count == 0)
        {
            return;
        }

        (CapturedOperation op, OrganizationServiceFault fault) = report.Failures
            .OrderBy(f => f.Op.Owner.PlannedIndex)
            .First();
        done = op.Owner.PlannedIndex;
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
    public CapturedOperation(OrganizationRequest request, BatchOwner owner, int seqInOwner, string category, bool isDelete)
    {
        Request = request;
        Owner = owner;
        SeqInOwner = seqInOwner;
        Category = category;
        IsDelete = isDelete;
    }

    public OrganizationRequest Request { get; }
    public BatchOwner Owner { get; }
    public int SeqInOwner { get; }
    public string Category { get; }
    public bool IsDelete { get; }
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
/// An <see cref="IOrganizationService"/> decorator that records every Create/Update/Delete/SetState the existing
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
        Guid id = entity.Id == Guid.Empty ? Guid.NewGuid() : entity.Id;
        entity.Id = id;
        _capturedCreatedIds.Add(id);
        CreateRequest request = new CreateRequest { Target = entity };
        ApplySolution(request);
        Capture(request, entity.LogicalName, "create", isDelete: false);
        return id;
    }

    public void Update(Entity entity)
    {
        UpdateRequest request = new UpdateRequest { Target = entity };
        ApplySolution(request);
        Capture(request, entity.LogicalName, "update", isDelete: false);
    }

    public void Delete(string entityName, Guid id)
    {
        DeleteRequest request = new DeleteRequest { Target = new EntityReference(entityName, id) };
        Capture(request, entityName, "delete", isDelete: true);
    }

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        if (request is SetStateRequest)
        {
            Capture(request, string.Empty, "setstate", isDelete: false);
            return new SetStateResponse();
        }

        return _inner.Execute(request);
    }

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

    private void Capture(OrganizationRequest request, string targetEntity, string verb, bool isDelete)
    {
        BatchOwner owner = _owner ?? throw new InvalidOperationException("BatchCapturingService: no owner set before a write.");
        int seq = _seqByOwner.TryGetValue(owner.Id, out int current) ? current : 0;
        _seqByOwner[owner.Id] = seq + 1;
        Operations.Add(new CapturedOperation(request, owner, seq, Categorize(targetEntity, verb, seq, owner.Kind), isDelete));
    }

    private static string Categorize(string targetEntity, string verb, int seq, string ownerKind)
    {
        if (verb == "setstate")
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
