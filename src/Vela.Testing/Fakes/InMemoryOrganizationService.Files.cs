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
    // The File/Image column block protocol (matched by RequestName; see ExecuteByName).
    public sealed partial class InMemoryOrganizationService
    {
        private OrganizationResponse InitializeFileBlocksUpload(OrganizationRequest request)
        {
            EntityReference target = GetParam<EntityReference>(request, "Target");
            string attribute = GetParam<string>(request, "FileAttributeName");
            string fileName = TryGetParam<string>(request, "FileName") ?? string.Empty;

            string token = "vela-upload-" + (++_tokenSequence).ToString();
            _uploads[token] = new UploadSession
            {
                EntityLogicalName = target.LogicalName,
                RecordId = target.Id,
                AttributeName = attribute,
                FileName = fileName
            };

            OrganizationResponse response = new OrganizationResponse { ResponseName = "InitializeFileBlocksUpload" };
            response.Results["FileContinuationToken"] = token;
            return response;
        }

        private OrganizationResponse UploadBlock(OrganizationRequest request)
        {
            UploadSession session = ResolveUpload(GetParam<string>(request, "FileContinuationToken"));
            string blockId = GetParam<string>(request, "BlockId");
            byte[] data = GetParam<byte[]>(request, "BlockData");
            session.Blocks[blockId] = (byte[])data.Clone();
            return new OrganizationResponse { ResponseName = "UploadBlock" };
        }

        private OrganizationResponse CommitFileBlocksUpload(OrganizationRequest request)
        {
            string token = GetParam<string>(request, "FileContinuationToken");
            UploadSession session = ResolveUpload(token);

            // Assemble the blocks in the order the caller lists them (falling back to upload order).
            IEnumerable<string> blockOrder = TryGetParam<object>(request, "BlockList") switch
            {
                IEnumerable<string> ids => ids,
                _ => session.Blocks.Keys
            };

            List<byte> content = new List<byte>();
            foreach (string id in blockOrder)
            {
                if (session.Blocks.TryGetValue(id, out byte[]? block))
                {
                    content.AddRange(block);
                }
            }

            byte[] bytes = content.ToArray();
            Guid fileId = _idSequence();
            _files[fileId] = new FileBlob
            {
                Content = bytes,
                FileName = TryGetParam<string>(request, "FileName") ?? session.FileName,
                MimeType = TryGetParam<string>(request, "MimeType") ?? "application/octet-stream",
                EntityLogicalName = session.EntityLogicalName,
                RecordId = session.RecordId,
                AttributeName = session.AttributeName,
            };

            // The File column stores its fileid; the content is fetched via the download messages.
            Update(new Entity(session.EntityLogicalName, session.RecordId) { [session.AttributeName] = fileId });
            _uploads.Remove(token);

            OrganizationResponse response = new OrganizationResponse { ResponseName = "CommitFileBlocksUpload" };
            response.Results["FileId"] = fileId;
            response.Results["FileSizeInBytes"] = (long)bytes.Length;
            return response;
        }

        private OrganizationResponse InitializeFileBlocksDownload(OrganizationRequest request)
        {
            EntityReference target = GetParam<EntityReference>(request, "Target");
            string attribute = GetParam<string>(request, "FileAttributeName");

            if (!Table(target.LogicalName).TryGetValue(target.Id, out Entity? record))
            {
                throw RecordDoesNotExist(target.LogicalName, target.Id);
            }

            // A file download must respect the same access control as Retrieve: a record-level Read demand and
            // field-level security on the File column. Without this a caller with no Read (or no access to a secured
            // File column) could stream content that Retrieve/RetrieveMultiple would have masked - a false green.
            if (!MembershipIntersectTables.Contains(target.LogicalName))
            {
                DemandAccess(target.LogicalName, Privilege.Read, record);
            }

            DemandSecuredColumnRead(target.LogicalName, attribute);
            DemandFullImageStored(target, attribute, record);

            if (!record.Contains(attribute) || record[attribute] == null)
            {
                // The platform's "nothing to download" fault (seen live on an Image column with no full image; not
                // probed on an empty File column).
                throw PlatformFault.Create(ObjectDoesNotExist,
                    $"No FileAttachment records found for '{target.LogicalName}.{attribute}' on record {target.Id}.");
            }

            (byte[] content, string fileName) = ResolveFileContent(record[attribute], $"{target.LogicalName}.{attribute}");
            string token = "vela-download-" + (++_tokenSequence).ToString();
            _downloads[token] = content;

            OrganizationResponse response = new OrganizationResponse { ResponseName = "InitializeFileBlocksDownload" };
            response.Results["FileContinuationToken"] = token;
            response.Results["FileName"] = fileName;
            response.Results["FileSizeInBytes"] = (long)content.Length;
            response.Results["IsChunkingSupported"] = true;
            return response;
        }

        // The platform keeps only a thumbnail for an Image column that does not store full images, so the block
        // download has nothing to serve and fails with 0x80040217 (verified live, 9.2.26094.133; account.entityimage
        // is such a column). Only registered metadata tells an Image column from inline bytes, so without an
        // ImageAttributeMetadata for the column the download keeps serving the stored bytes. The platform applies
        // the setting in force when the image was set; the harness reads it at download time.
        private void DemandFullImageStored(EntityReference target, string attribute, Entity record)
        {
            if (!_metadata.TryGetValue(target.LogicalName, out EntityMetadata? metadata)
                || metadata.Attributes?.FirstOrDefault(a => string.Equals(a.LogicalName, attribute, StringComparison.OrdinalIgnoreCase))
                    is not ImageAttributeMetadata image
                || image.CanStoreFullImage == true)
            {
                return;
            }

            Guid descriptorId = record.GetAttributeValue<Guid>(attribute + "id");
            string message =
                $"No FileAttachment records found for imagedescriptorId: {descriptorId:D} for image attribute: {attribute} " +
                $"of {target.LogicalName} record with id {target.Id:D}";
            throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = ObjectDoesNotExist, Message = message },
                new FaultReason(message));
        }

        private OrganizationResponse DownloadBlock(OrganizationRequest request)
        {
            string token = GetParam<string>(request, "FileContinuationToken");
            if (!_downloads.TryGetValue(token, out byte[]? content))
            {
                throw new InvalidOperationException($"Unknown file continuation token '{token}'. Call InitializeFileBlocksDownload first.");
            }

            long offset = Convert.ToInt64(TryGetParam<object>(request, "Offset") ?? 0L);
            long requested = Convert.ToInt64(TryGetParam<object>(request, "BlockLength") ?? (long)content.Length);
            int start = (int)Math.Min(Math.Max(offset, 0L), content.Length);
            int count = (int)Math.Min(Math.Max(requested, 0L), content.Length - start);
            byte[] slice = new byte[count];
            Array.Copy(content, start, slice, 0, count);

            OrganizationResponse response = new OrganizationResponse { ResponseName = "DownloadBlock" };
            response.Results["Data"] = slice;
            return response;
        }

        private OrganizationResponse DeleteFile(OrganizationRequest request)
        {
            Guid fileId = GetParam<Guid>(request, "FileId");
            // A file whose record is gone is unknown too (one uploaded to a record deleted mid-upload): without the
            // record there is nothing to check Write against.
            Entity? record = _files.TryGetValue(fileId, out FileBlob? blob) ? StoredOrNull(blob.EntityLogicalName, blob.RecordId) : null;
            if (blob == null || record == null)
            {
                throw PlatformFault.Create(ObjectDoesNotExist, $"File with Id = {fileId:D} Does Not Exist");
            }

            // Deleting a file clears its File column: it needs Write on that record (and on the column when it is
            // secured), and touches no other column or table, even one holding the same Guid.
            DemandAccess(blob.EntityLogicalName, Privilege.Write, record);
            DemandSecuredColumnWrites(new Entity(blob.EntityLogicalName, blob.RecordId) { [blob.AttributeName] = null }, onCreate: false);
            if (record.GetAttributeValue<Guid?>(blob.AttributeName) == fileId)
            {
                record.Attributes.Remove(blob.AttributeName);
            }

            _files.Remove(fileId);
            return new OrganizationResponse { ResponseName = "DeleteFile" };
        }

        // File column -> its content lives in the blob store keyed by fileid. Image column (or any column that
        // holds bytes inline) -> the stored bytes ARE the content/thumbnail.
        private (byte[] Content, string FileName) ResolveFileContent(object value, string column)
        {
            switch (value)
            {
                case Guid fileId when _files.TryGetValue(fileId, out FileBlob? blob):
                    return (blob.Content, blob.FileName);
                case byte[] bytes:
                    return (bytes, string.Empty);
                default:
                    throw new InvalidOperationException(
                        $"'{column}' does not hold a known fileid or inline bytes; upload a file first or set the column to a byte[].");
            }
        }

        private UploadSession ResolveUpload(string token)
        {
            if (!_uploads.TryGetValue(token, out UploadSession? session))
            {
                throw new InvalidOperationException($"Unknown file continuation token '{token}'. Call InitializeFileBlocksUpload first.");
            }

            return session;
        }

        private static T GetParam<T>(OrganizationRequest request, string name)
        {
            if (!request.Parameters.TryGetValue(name, out object? value) || value is not T typed)
            {
                throw new InvalidOperationException($"Request '{request.RequestName}' requires a '{name}' parameter of type {typeof(T).Name}.");
            }

            return typed;
        }

        private static T? TryGetParam<T>(OrganizationRequest request, string name)
            where T : class
            => request.Parameters.TryGetValue(name, out object? value) && value is T typed ? typed : null;

        internal sealed class FileBlob
        {
            public byte[] Content = Array.Empty<byte>();
            public string FileName = string.Empty;
            public string MimeType = string.Empty;

            // The File column the blob was uploaded to: DeleteFile clears that column only.
            public string EntityLogicalName = string.Empty;
            public Guid RecordId;
            public string AttributeName = string.Empty;
        }

        private sealed class UploadSession
        {
            public string EntityLogicalName = string.Empty;
            public Guid RecordId;
            public string AttributeName = string.Empty;
            public string FileName = string.Empty;
            public readonly Dictionary<string, byte[]> Blocks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }
    }
}
