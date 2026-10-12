using System;
using System.Collections.Generic;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Cli.Tests.TestSupport;

/// <summary>
/// The platform side of a plugin package's stored file: Dataverse keeps the uploaded .nupkg in the pluginpackage
/// `package` file column and empties `content` (verified live), and serves the file through
/// InitializeFileBlocksDownload / DownloadBlock. The fakes store a package's bytes here when `content` is written.
/// </summary>
internal sealed class PackageFileStore
{
    // Dataverse "No FileAttachment records found" (0x80040217) for a record with no stored file.
    private const int NoFileErrorCode = unchecked((int)0x80040217);

    private readonly Dictionary<Guid, byte[]> _files = new();

    /// <summary>Every block download served, as (offset, length).</summary>
    public List<(long Offset, long Length)> BlockDownloads { get; } = new();

    /// <summary>What InitializeFileBlocksDownload reports as IsChunkingSupported. When false, like the platform, a
    /// DownloadBlock must request the whole file in one block from offset 0.</summary>
    public bool ChunkingSupported { get; set; } = true;

    public void Store(Guid packageId, byte[] bytes) => _files[packageId] = bytes;

    /// <summary>Stores the package a pluginpackage write uploads (its base64 `content`), if it carries one.</summary>
    public void StoreUpload(Guid packageId, Entity write)
    {
        if (string.Equals(write.LogicalName, "pluginpackage", StringComparison.OrdinalIgnoreCase)
            && write.GetAttributeValue<string>("content") is { Length: > 0 } content)
        {
            _files[packageId] = Convert.FromBase64String(content);
        }
    }

    public bool TryExecute(OrganizationRequest request, out OrganizationResponse response)
    {
        switch (request)
        {
            case InitializeFileBlocksDownloadRequest init:
                if (!_files.TryGetValue(init.Target.Id, out byte[]? file))
                {
                    throw new FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { ErrorCode = NoFileErrorCode, Message = "No FileAttachment records found." },
                        "No FileAttachment records found.");
                }

                response = new InitializeFileBlocksDownloadResponse
                {
                    Results =
                    {
                        ["FileContinuationToken"] = init.Target.Id.ToString(),
                        ["FileSizeInBytes"] = file.LongLength,
                        ["FileName"] = "package.nupkg",
                        ["IsChunkingSupported"] = ChunkingSupported,
                    },
                };
                return true;
            case DownloadBlockRequest block:
                byte[] stored = _files[Guid.Parse(block.FileContinuationToken)];
                if (!ChunkingSupported && (block.Offset != 0 || block.BlockLength < stored.LongLength))
                {
                    throw new FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { Message = "Chunking is not supported: download the whole file in one block." },
                        "Chunking is not supported: download the whole file in one block.");
                }

                long length = Math.Min(block.BlockLength, stored.LongLength - block.Offset);
                BlockDownloads.Add((block.Offset, length));
                response = new DownloadBlockResponse { Results = { ["Data"] = stored.AsSpan((int)block.Offset, (int)length).ToArray() } };
                return true;
            default:
                response = null!;
                return false;
        }
    }
}
