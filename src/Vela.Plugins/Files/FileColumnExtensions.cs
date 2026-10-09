using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Files
{
    /// <summary>
    /// One-call helpers for File/Image columns over an <see cref="IOrganizationService"/>: they drive the
    /// platform's block upload/download protocol (Initialize → block loop → Commit, and Delete) so a plugin
    /// can read or write a whole file in a single call instead of chaining the messages by hand. The messages
    /// are issued by name (<c>InitializeFileBlocksUpload</c>, <c>UploadBlock</c>, …), which works against both a
    /// live Dataverse org and the in-memory test harness.
    /// </summary>
    public static class FileColumnExtensions
    {
        // Dataverse caps a single block at 4 MB; upload/download in blocks of this size by default.
        private const int DefaultBlockSize = 4 * 1024 * 1024;

        /// <summary>The largest block Dataverse accepts (4 MB); a larger requested block size is capped to it.</summary>
        public const int MaxBlockSize = DefaultBlockSize;

        /// <summary>
        /// Uploads <paramref name="content"/> into a File column in one call (initialize, upload blocks, commit)
        /// and returns the new file id (the value the column then stores).
        /// </summary>
        /// <param name="service">The organization service.</param>
        /// <param name="target">The record that owns the File column.</param>
        /// <param name="fileAttributeName">The File column's logical name.</param>
        /// <param name="fileName">The file name to store.</param>
        /// <param name="content">The file bytes.</param>
        /// <param name="mimeType">The content MIME type (default <c>application/octet-stream</c>).</param>
        /// <param name="blockSize">The block size in bytes (default 4 MB, the platform maximum; larger values are capped).</param>
        /// <returns>The committed file id.</returns>
        public static Guid UploadFile(
            this IOrganizationService service,
            EntityReference target,
            string fileAttributeName,
            string fileName,
            byte[] content,
            string mimeType = "application/octet-stream",
            int blockSize = DefaultBlockSize)
        {
            service = service ?? throw new ArgumentNullException(nameof(service));
            target = target ?? throw new ArgumentNullException(nameof(target));
            content = content ?? throw new ArgumentNullException(nameof(content));
            if (string.IsNullOrWhiteSpace(fileAttributeName))
            {
                throw new ArgumentNullException(nameof(fileAttributeName));
            }

            blockSize = NormalizeBlockSize(blockSize);

            OrganizationRequest init = new OrganizationRequest("InitializeFileBlocksUpload");
            init["Target"] = target;
            init["FileAttributeName"] = fileAttributeName;
            init["FileName"] = fileName ?? string.Empty;
            string token = (string)service.Execute(init).Results["FileContinuationToken"];

            List<string> blockIds = new List<string>();
            for (int offset = 0, index = 0; offset < content.Length; offset += blockSize, index++)
            {
                int size = Math.Min(blockSize, content.Length - offset);
                byte[] block = new byte[size];
                Array.Copy(content, offset, block, 0, size);

                // Block ids must be base64 strings of equal length; a fixed-width counter satisfies both.
                string blockId = Convert.ToBase64String(BitConverter.GetBytes((long)index));
                OrganizationRequest upload = new OrganizationRequest("UploadBlock");
                upload["FileContinuationToken"] = token;
                upload["BlockId"] = blockId;
                upload["BlockData"] = block;
                service.Execute(upload);
                blockIds.Add(blockId);
            }

            OrganizationRequest commit = new OrganizationRequest("CommitFileBlocksUpload");
            commit["FileContinuationToken"] = token;
            commit["FileName"] = fileName ?? string.Empty;
            commit["MimeType"] = mimeType ?? "application/octet-stream";
            commit["BlockList"] = blockIds.ToArray();
            return (Guid)service.Execute(commit).Results["FileId"];
        }

        /// <summary>
        /// Downloads the whole content of a File/Image column in one call (initialize, then loop over blocks).
        /// </summary>
        /// <param name="service">The organization service.</param>
        /// <param name="target">The record that owns the column.</param>
        /// <param name="fileAttributeName">The File/Image column's logical name.</param>
        /// <param name="blockSize">
        /// The block size in bytes for a chunked download (default 4 MB, the platform maximum; larger values are capped).
        /// When the platform reports <c>IsChunkingSupported = false</c>, the whole file is read in one block instead.
        /// </param>
        /// <returns>The file bytes.</returns>
        public static byte[] DownloadFile(
            this IOrganizationService service,
            EntityReference target,
            string fileAttributeName,
            int blockSize = DefaultBlockSize)
        {
            service = service ?? throw new ArgumentNullException(nameof(service));
            target = target ?? throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(fileAttributeName))
            {
                throw new ArgumentNullException(nameof(fileAttributeName));
            }

            blockSize = NormalizeBlockSize(blockSize);

            OrganizationRequest init = new OrganizationRequest("InitializeFileBlocksDownload");
            init["Target"] = target;
            init["FileAttributeName"] = fileAttributeName;
            ParameterCollection initialized = service.Execute(init).Results;
            string token = (string)initialized["FileContinuationToken"];
            long size = Convert.ToInt64(initialized["FileSizeInBytes"]);

            // Without chunking support Dataverse serves the file only as one block covering the whole file, so the
            // 4 MB cap applies to chunked downloads only. An absent flag means chunking is supported.
            bool chunked = !initialized.Contains("IsChunkingSupported") || !(initialized["IsChunkingSupported"] is bool supported) || supported;
            long blockLength = chunked ? blockSize : Math.Max(size, 1);

            // Collect into a stream sized to the announced length: one buffer copy per block instead of a List<byte>
            // filled byte by byte.
            using MemoryStream content = new MemoryStream(size > 0 && size < int.MaxValue ? (int)size : 0);
            for (long offset = 0; offset < size;)
            {
                long length = Math.Min(blockLength, size - offset);
                OrganizationRequest download = new OrganizationRequest("DownloadBlock");
                download["FileContinuationToken"] = token;
                download["Offset"] = offset;
                download["BlockLength"] = length;
                byte[] data = (byte[])service.Execute(download).Results["Data"];
                if (data == null || data.Length == 0)
                {
                    // An empty block before the announced size is a short read, not the end of the file.
                    break;
                }

                content.Write(data, 0, data.Length);
                offset += data.Length;
            }

            // Never hand back a truncated (or overlong) file as if it were whole: the caller could persist it.
            if (content.Length != size)
            {
                throw new InvalidPluginExecutionException(
                    $"Download of column '{fileAttributeName}' on '{target.LogicalName}' ({target.Id}) returned " +
                    $"{content.Length} byte(s) but the platform announced {size}.");
            }

            return content.ToArray();
        }

        // A non-positive size means "use the default"; anything above the platform maximum is capped, because
        // Dataverse rejects a block larger than 4 MB.
        private static int NormalizeBlockSize(int blockSize) =>
            blockSize <= 0 || blockSize > MaxBlockSize ? MaxBlockSize : blockSize;

        /// <summary>Deletes a stored file by its file id.</summary>
        /// <param name="service">The organization service.</param>
        /// <param name="fileId">The file id (the value a File column stores).</param>
        public static void DeleteFile(this IOrganizationService service, Guid fileId)
        {
            service = service ?? throw new ArgumentNullException(nameof(service));
            OrganizationRequest delete = new OrganizationRequest("DeleteFile");
            delete["FileId"] = fileId;
            service.Execute(delete);
        }

        /// <summary>
        /// Deletes the file referenced by a File column (reads the column's file id, then deletes it). Does
        /// nothing when the column holds no file.
        /// </summary>
        /// <param name="service">The organization service.</param>
        /// <param name="target">The record that owns the File column.</param>
        /// <param name="fileAttributeName">The File column's logical name.</param>
        public static void DeleteFile(this IOrganizationService service, EntityReference target, string fileAttributeName)
        {
            service = service ?? throw new ArgumentNullException(nameof(service));
            target = target ?? throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(fileAttributeName))
            {
                throw new ArgumentNullException(nameof(fileAttributeName));
            }

            Entity record = service.Retrieve(target.LogicalName, target.Id, new ColumnSet(fileAttributeName));
            if (!record.Contains(fileAttributeName) || record[fileAttributeName] == null)
            {
                // No file on the column: nothing to delete.
                return;
            }

            object raw = record[fileAttributeName];
            if (raw is Guid fileId)
            {
                if (fileId != Guid.Empty)
                {
                    service.DeleteFile(fileId);
                }

                return;
            }

            // The column read back as a non-null, non-Guid value (e.g. the wrong column name was passed, or it is not
            // a File column). Fail loudly instead of silently no-opping so the caller learns the delete did not happen.
            throw new InvalidPluginExecutionException(
                $"Column '{fileAttributeName}' on '{target.LogicalName}' did not read back as a file id " +
                $"(got '{raw.GetType().FullName}'); it may not be a File column. No file was deleted.");
        }
    }
}
