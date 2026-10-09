using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Vela.Context;
using Vela.Files;
using Vela.Querying;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Runtime polish: organization services created on first use, RetrieveAll leaving the caller's paging untouched,
// and file blocks capped at the 4 MB platform maximum.
public sealed class RuntimePerfTests
{
    private static (LocalPluginContext Context, Mock<IOrganizationServiceFactory> Factory) ContextWithFactory()
    {
        Mock<IOrganizationServiceFactory> factory = new Mock<IOrganizationServiceFactory>();
        factory.Setup(f => f.CreateOrganizationService(It.IsAny<Guid?>())).Returns(Mock.Of<IOrganizationService>());
        Mock<IServiceProvider> provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IPluginExecutionContext))).Returns(new FakePluginExecutionContext());
        provider.Setup(p => p.GetService(typeof(IOrganizationServiceFactory))).Returns(factory.Object);
        return (new LocalPluginContext(provider.Object), factory);
    }

    [Fact]
    public void Organization_services_are_created_on_first_use_and_only_once()
    {
        (LocalPluginContext ctx, Mock<IOrganizationServiceFactory> factory) = ContextWithFactory();

        factory.Verify(f => f.CreateOrganizationService(It.IsAny<Guid?>()), Times.Never);

        IOrganizationService user = ctx.Service;
        Assert.Same(user, ctx.Service);
        factory.Verify(f => f.CreateOrganizationService(It.IsAny<Guid?>()), Times.Once);

        IOrganizationService system = ctx.SystemService;
        Assert.Same(system, ctx.SystemService);
        factory.Verify(f => f.CreateOrganizationService(null), Times.Once);
    }

    [Fact]
    public void The_notification_service_falls_back_to_a_null_service_when_the_platform_has_none()
    {
        (LocalPluginContext ctx, _) = ContextWithFactory();

        Assert.NotNull(ctx.NotificationService);
        Assert.Same(ctx.NotificationService, ctx.NotificationService);
    }

    [Fact]
    public void RetrieveAll_restores_the_callers_paging()
    {
        InMemoryOrganizationService org = new InMemoryOrganizationService();
        for (int i = 0; i < 5; i++)
        {
            org.Create(new Entity("account") { ["name"] = "A" + i });
        }

        TypedQuery<TestAccount> query = Query.For<TestAccount>().Select(a => a.Name).Page(1, 2);
        PagingInfo original = query.Expression.PageInfo;

        List<TestAccount> all = org.RetrieveAll(query, pageSize: 2);

        Assert.Equal(5, all.Count);
        Assert.Same(original, query.Expression.PageInfo);
        Assert.Equal(1, query.Expression.PageInfo.PageNumber);
        Assert.Equal(2, query.Expression.PageInfo.Count);
        Assert.Null(query.Expression.PageInfo.PagingCookie);

        // A second run of the same query still starts from the first page.
        Assert.Equal(5, org.RetrieveAll(query, pageSize: 2).Count);
    }

    // Records the block sizes the helpers send and serves a fixed file for download.
    private sealed class BlockRecordingService : IOrganizationService
    {
        public List<int> UploadedBlockSizes { get; } = new List<int>();

        public List<long> RequestedDownloadLengths { get; } = new List<long>();

        public long FileSize { get; set; }

        // Null leaves IsChunkingSupported out of the initialize response.
        public bool? ChunkingSupported { get; set; }

        // Overrides the bytes served for a DownloadBlock (offset, requested length); null serves the requested length.
        public Func<long, long, byte[]?>? ServeBlock { get; set; }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            OrganizationResponse response = new OrganizationResponse();
            switch (request.RequestName)
            {
                case "InitializeFileBlocksUpload":
                    response.Results["FileContinuationToken"] = "token";
                    break;
                case "UploadBlock":
                    UploadedBlockSizes.Add(((byte[])request["BlockData"]).Length);
                    break;
                case "CommitFileBlocksUpload":
                    response.Results["FileId"] = Guid.NewGuid();
                    break;
                case "InitializeFileBlocksDownload":
                    response.Results["FileContinuationToken"] = "token";
                    response.Results["FileSizeInBytes"] = FileSize;
                    if (ChunkingSupported.HasValue)
                    {
                        response.Results["IsChunkingSupported"] = ChunkingSupported.Value;
                    }
                    break;
                case "DownloadBlock":
                    long length = (long)request["BlockLength"];
                    RequestedDownloadLengths.Add(length);
                    response.Results["Data"] = ServeBlock != null
                        ? ServeBlock((long)request["Offset"], length)
                        : Enumerable.Repeat((byte)7, (int)length).ToArray();
                    break;
            }

            return response;
        }

        public Guid Create(Entity entity) => throw new NotSupportedException();

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotSupportedException();

        public void Update(Entity entity) => throw new NotSupportedException();

        public void Delete(string entityName, Guid id) => throw new NotSupportedException();

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotSupportedException();

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotSupportedException();

        public EntityCollection RetrieveMultiple(QueryBase query) => throw new NotSupportedException();
    }

    [Fact]
    public void A_block_size_above_4_MB_is_capped_for_upload_and_download()
    {
        BlockRecordingService service = new BlockRecordingService { FileSize = FileColumnExtensions.MaxBlockSize + 10L };
        byte[] content = new byte[FileColumnExtensions.MaxBlockSize + 10];
        EntityReference target = new EntityReference("account", Guid.NewGuid());

        service.UploadFile(target, "new_file", "big.bin", content, blockSize: int.MaxValue);
        byte[] downloaded = service.DownloadFile(target, "new_file", blockSize: 64 * 1024 * 1024);

        Assert.Equal(new[] { FileColumnExtensions.MaxBlockSize, 10 }, service.UploadedBlockSizes);
        Assert.Equal(new[] { (long)FileColumnExtensions.MaxBlockSize, 10L }, service.RequestedDownloadLengths);
        Assert.Equal(FileColumnExtensions.MaxBlockSize + 10, downloaded.Length);
        Assert.All(downloaded.Take(16), b => Assert.Equal(7, b));
    }

    [Fact]
    public void Without_chunking_support_the_whole_file_is_downloaded_in_one_block_even_above_4_MB()
    {
        long size = FileColumnExtensions.MaxBlockSize + 1024L;
        BlockRecordingService service = new BlockRecordingService { FileSize = size, ChunkingSupported = false };

        byte[] downloaded = service.DownloadFile(new EntityReference("account", Guid.NewGuid()), "new_file");

        Assert.Equal(new[] { size }, service.RequestedDownloadLengths); // one block covering the file
        Assert.Equal(size, downloaded.LongLength);
    }

    [Fact]
    public void With_chunking_reported_explicitly_the_4_MB_cap_still_applies()
    {
        BlockRecordingService service = new BlockRecordingService { FileSize = FileColumnExtensions.MaxBlockSize + 1L, ChunkingSupported = true };

        service.DownloadFile(new EntityReference("account", Guid.NewGuid()), "new_file", blockSize: int.MaxValue);

        Assert.Equal(new[] { (long)FileColumnExtensions.MaxBlockSize, 1L }, service.RequestedDownloadLengths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_empty_block_before_the_announced_size_fails_instead_of_returning_a_truncated_file(bool chunked)
    {
        // Chunked: the second block comes back empty. Single block: the only block comes back empty.
        BlockRecordingService service = new BlockRecordingService
        {
            FileSize = 10,
            ChunkingSupported = chunked,
            ServeBlock = (offset, length) => offset == 0 && chunked ? new byte[length] : Array.Empty<byte>(),
        };
        EntityReference target = new EntityReference("account", Guid.NewGuid());

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => service.DownloadFile(target, "new_file", blockSize: 4));

        Assert.Contains("'new_file'", ex.Message);
        Assert.Contains("'account'", ex.Message);
        Assert.Contains(target.Id.ToString(), ex.Message);
        Assert.Contains($"received {(chunked ? 4 : 0)} byte(s)", ex.Message);
        Assert.Contains("file is 10 byte(s)", ex.Message);
    }

    [Fact]
    public void A_null_block_before_the_announced_size_fails()
    {
        BlockRecordingService service = new BlockRecordingService { FileSize = 3, ServeBlock = (_, _) => null };

        Assert.Throws<InvalidPluginExecutionException>(
            () => service.DownloadFile(new EntityReference("account", Guid.NewGuid()), "new_file"));
    }

    [Fact]
    public void A_block_longer_than_requested_fails_instead_of_returning_extra_bytes()
    {
        BlockRecordingService service = new BlockRecordingService { FileSize = 3, ServeBlock = (_, length) => new byte[length + 2] };

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => service.DownloadFile(new EntityReference("account", Guid.NewGuid()), "new_file"));

        Assert.Contains("received 5 byte(s)", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_exact_size_download_returns_the_whole_file(bool chunked)
    {
        BlockRecordingService service = new BlockRecordingService
        {
            FileSize = 10,
            ChunkingSupported = chunked,
            ServeBlock = (offset, length) => Enumerable.Range((int)offset, (int)length).Select(i => (byte)i).ToArray(),
        };

        byte[] downloaded = service.DownloadFile(new EntityReference("account", Guid.NewGuid()), "new_file", blockSize: 4);

        Assert.Equal(Enumerable.Range(0, 10).Select(i => (byte)i), downloaded);
        Assert.Equal(chunked ? new[] { 4L, 4L, 2L } : new[] { 10L }, service.RequestedDownloadLengths);
    }

    [Fact]
    public void An_empty_file_downloads_without_requesting_a_block()
    {
        BlockRecordingService service = new BlockRecordingService { FileSize = 0 };

        Assert.Empty(service.DownloadFile(new EntityReference("account", Guid.NewGuid()), "new_file"));
        Assert.Empty(service.RequestedDownloadLengths);
    }
}
