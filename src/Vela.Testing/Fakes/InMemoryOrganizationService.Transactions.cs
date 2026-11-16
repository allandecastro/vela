using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;

namespace Vela.Testing
{
    // ExecuteMultiple, ExecuteTransaction and the store snapshot/restore that makes an operation atomic (also used by the
    // pipeline simulator to roll back a failed operation).
    public sealed partial class InMemoryOrganizationService
    {
        // Runs each child request in order. ContinueOnError keeps going past a fault; ReturnResponses collects
        // per-item responses. Mirrors the platform's ExecuteMultiple (no transaction — see ExecuteTransaction).
        private ExecuteMultipleResponse ExecuteMultiple(ExecuteMultipleRequest request)
        {
            bool continueOnError = request.Settings?.ContinueOnError ?? false;
            bool returnResponses = request.Settings?.ReturnResponses ?? false;
            ExecuteMultipleResponseItemCollection items = new ExecuteMultipleResponseItemCollection();
            bool faulted = false;

            for (int i = 0; i < request.Requests.Count; i++)
            {
                try
                {
                    OrganizationResponse childResponse = Execute(request.Requests[i]);
                    if (returnResponses)
                    {
                        items.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = childResponse });
                    }
                }
                catch (Exception ex)
                {
                    faulted = true;
                    items.Add(new ExecuteMultipleResponseItem
                    {
                        RequestIndex = i,
                        // Carry the platform fault's ErrorCode as well as its Message: real plugins branch on the
                        // ErrorCode of a per-item fault (e.g. ObjectDoesNotExist), so a message-only fault would
                        // hide the code the caller inspects.
                        Fault = ExecuteMultipleItemFault(ex),
                    });

                    if (!continueOnError)
                    {
                        break;
                    }
                }
            }

            ExecuteMultipleResponse response = new ExecuteMultipleResponse();
            response.Results["IsFaulted"] = faulted;
            response.Results["Responses"] = items;
            return response;
        }

        // Builds the per-item fault for a faulted child request. A FaultException<OrganizationServiceFault> carries
        // the platform fault (ErrorCode + Message) in its Detail, so that is copied through verbatim - mirroring the
        // real ExecuteMultiple. Any other exception has no ErrorCode, so only its message is surfaced.
        private static OrganizationServiceFault ExecuteMultipleItemFault(Exception exception)
        {
            if (exception is FaultException<OrganizationServiceFault> fault && fault.Detail != null)
            {
                return new OrganizationServiceFault
                {
                    ErrorCode = fault.Detail.ErrorCode,
                    Message = fault.Detail.Message,
                };
            }

            return new OrganizationServiceFault { Message = exception.Message };
        }

        // Runs an operation all-or-nothing: snapshot the store, run it, and roll the whole store back if it throws, so
        // a multi-row/multi-record write (bulk create/update, deep insert) leaves no partial rows on a mid-way fault.
        internal T RunAtomically<T>(Func<T> operation)
        {
            StoreSnapshot snapshot = Snapshot();
            try
            {
                return operation();
            }
            catch
            {
                RestoreTo(snapshot);
                throw;
            }
        }

        // All-or-nothing: snapshot the store, run every child request, and roll back the whole batch if any throws.
        private ExecuteTransactionResponse ExecuteTransaction(ExecuteTransactionRequest request)
        {
            StoreSnapshot snapshot = Snapshot();
            OrganizationResponseCollection responses = new OrganizationResponseCollection();

            int index = 0;
            try
            {
                for (; index < request.Requests.Count; index++)
                {
                    OrganizationResponse childResponse = Execute(request.Requests[index]);
                    if (request.ReturnResponses == true)
                    {
                        responses.Add(childResponse);
                    }
                }
            }
            catch (Exception ex)
            {
                RestoreTo(snapshot);
                FaultException<OrganizationServiceFault>? wrapped = PlatformFault.ExecuteTransactionFailed(ex, index);
                if (wrapped != null)
                {
                    throw wrapped;
                }

                throw;
            }

            ExecuteTransactionResponse response = new ExecuteTransactionResponse();
            response.Results["Responses"] = responses;
            return response;
        }

        // Captures ALL mutable stores so a transaction/operation rollback is complete: the record store, the sharing
        // (PoA) table, the access-team grants, the seeded rows still derived from the setup, the file blobs, and the
        // row-version counter. A partial snapshot would
        // leave a rolled-back batch with, say, a share or a file that its failed write had created. Auto-number
        // counters and open upload/download sessions are left out on purpose: the platform does not roll an
        // auto-number back either (a failed create still consumes its number), and a block upload is not part of
        // the transaction.
        internal StoreSnapshot Snapshot()
        {
            StoreSnapshot copy = new StoreSnapshot
            {
                Sharing = _sharing.Clone(),
                AccessTeams = _accessTeams.Clone(),
                SeededRows = new Dictionary<Guid, string>(_seededRows),
                Version = _version,
            };

            foreach (KeyValuePair<string, Dictionary<Guid, Entity>> table in _store)
            {
                Dictionary<Guid, Entity> inner = new Dictionary<Guid, Entity>();
                foreach (KeyValuePair<Guid, Entity> record in table.Value)
                {
                    inner[record.Key] = EntityValues.Clone(record.Value);
                }

                copy.Store[table.Key] = inner;
            }

            foreach (KeyValuePair<Guid, FileBlob> file in _files)
            {
                copy.Files[file.Key] = CloneFileBlob(file.Value);
            }

            return copy;
        }

        internal void RestoreTo(StoreSnapshot snapshot)
        {
            _store.Clear();
            foreach (KeyValuePair<string, Dictionary<Guid, Entity>> table in snapshot.Store)
            {
                Dictionary<Guid, Entity> inner = new Dictionary<Guid, Entity>();
                foreach (KeyValuePair<Guid, Entity> record in table.Value)
                {
                    inner[record.Key] = EntityValues.Clone(record.Value);
                }

                _store[table.Key] = inner;
            }

            _sharing.CopyFrom(snapshot.Sharing);
            _accessTeams.CopyFrom(snapshot.AccessTeams);

            // A rolled-back cascade gives the seeded rows it reached back to the setup.
            _seededRows = new Dictionary<Guid, string>(snapshot.SeededRows);

            _files.Clear();
            foreach (KeyValuePair<Guid, FileBlob> file in snapshot.Files)
            {
                _files[file.Key] = CloneFileBlob(file.Value);
            }

            _version = snapshot.Version;
        }

        private static FileBlob CloneFileBlob(FileBlob blob) => new FileBlob
        {
            Content = (byte[])blob.Content.Clone(),
            FileName = blob.FileName,
            MimeType = blob.MimeType,
            EntityLogicalName = blob.EntityLogicalName,
            RecordId = blob.RecordId,
            AttributeName = blob.AttributeName,
        };

        // A point-in-time deep copy of every mutable store, taken by Snapshot() and re-applied by RestoreTo() so an
        // ExecuteTransaction (or any pipeline operation) rolls back cleanly. Nested so it can reference FileBlob.
        internal sealed class StoreSnapshot
        {
            public Dictionary<string, Dictionary<Guid, Entity>> Store { get; } =
                new Dictionary<string, Dictionary<Guid, Entity>>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<Guid, FileBlob> Files { get; } = new Dictionary<Guid, FileBlob>();

            public SharingTable Sharing { get; set; } = new SharingTable();

            public AccessTeamGrants AccessTeams { get; set; } = new AccessTeamGrants();

            public Dictionary<Guid, string> SeededRows { get; set; } = new Dictionary<Guid, string>();

            public long Version { get; set; }
        }
    }
}
