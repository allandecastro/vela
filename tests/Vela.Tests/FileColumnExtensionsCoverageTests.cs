using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Files;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Covers the File/Image column block-protocol helpers against the in-memory harness: argument guards,
// multi-block upload/download loops, block-size normalization, and both DeleteFile overloads.
public sealed class FileColumnExtensionsCoverageTests
{
    private static (InMemoryOrganizationService Crm, EntityReference Target) Seeded()
    {
        Guid id = Guid.NewGuid();
        InMemoryOrganizationService crm = new InMemoryOrganizationService()
            .Seed(new Entity("account") { Id = id, ["name"] = "Acme" });
        return (crm, new EntityReference("account", id));
    }

    [Fact]
    public void UploadFile_validates_its_arguments()
    {
        (InMemoryOrganizationService crm, EntityReference target) = Seeded();
        byte[] content = { 1, 2, 3 };

        Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).UploadFile(target, "new_file", "f", content));
        Assert.Throws<ArgumentNullException>(() => crm.UploadFile(null!, "new_file", "f", content));
        Assert.Throws<ArgumentNullException>(() => crm.UploadFile(target, "new_file", "f", null!));
        Assert.Throws<ArgumentNullException>(() => crm.UploadFile(target, "  ", "f", content));
    }

    [Fact]
    public void UploadFile_then_DownloadFile_round_trips_across_multiple_blocks()
    {
        (InMemoryOrganizationService crm, EntityReference target) = Seeded();
        byte[] content = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();

        // blockSize 4 forces three upload blocks; null fileName/mimeType exercise the default fallbacks.
        Guid fileId = crm.UploadFile(target, "new_file", null!, content, mimeType: null!, blockSize: 4);
        Assert.NotEqual(Guid.Empty, fileId);

        byte[] downloaded = crm.DownloadFile(target, "new_file", blockSize: 4);
        Assert.Equal(content, downloaded);
    }

    [Fact]
    public void UploadFile_and_DownloadFile_normalize_a_non_positive_block_size()
    {
        (InMemoryOrganizationService crm, EntityReference target) = Seeded();
        byte[] content = { 9, 8, 7 };

        Guid fileId = crm.UploadFile(target, "new_file", "f.bin", content, blockSize: 0); // <= 0 -> default
        Assert.NotEqual(Guid.Empty, fileId);
        Assert.Equal(content, crm.DownloadFile(target, "new_file", blockSize: -5));
    }

    [Fact]
    public void DownloadFile_validates_its_arguments()
    {
        (InMemoryOrganizationService crm, EntityReference target) = Seeded();
        Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).DownloadFile(target, "new_file"));
        Assert.Throws<ArgumentNullException>(() => crm.DownloadFile(null!, "new_file"));
        Assert.Throws<ArgumentNullException>(() => crm.DownloadFile(target, " "));
    }

    [Fact]
    public void DeleteFile_by_id_clears_the_blob()
    {
        (InMemoryOrganizationService crm, EntityReference target) = Seeded();
        Guid fileId = crm.UploadFile(target, "new_file", "f.bin", new byte[] { 1 });

        Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).DeleteFile(fileId));
        crm.DeleteFile(fileId); // must not throw; the column reference is cleared by the harness
    }

    [Fact]
    public void DeleteFile_by_column_deletes_when_present_and_is_a_no_op_when_empty()
    {
        (InMemoryOrganizationService crm, EntityReference target) = Seeded();
        crm.UploadFile(target, "new_file", "f.bin", new byte[] { 1, 2 });

        Assert.Throws<ArgumentNullException>(() => crm.DeleteFile(target, "  "));
        Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).DeleteFile(target, "new_file"));

        crm.DeleteFile(target, "new_file");          // file present -> deleted
        crm.DeleteFile(target, "new_file");          // now empty -> no-op (does not throw)
    }

    [Fact]
    public void DeleteFile_by_column_throws_when_the_column_does_not_read_back_as_a_file_id()
    {
        // A column that reads back as a non-null, non-Guid value (wrong column name, or not a File column) must fail
        // loudly rather than silently no-op, so the caller learns the delete did not happen.
        Guid id = Guid.NewGuid();
        Moq.Mock<IOrganizationService> service = new Moq.Mock<IOrganizationService>();
        service
            .Setup(s => s.Retrieve("account", id, Moq.It.IsAny<Microsoft.Xrm.Sdk.Query.ColumnSet>()))
            .Returns(new Entity("account", id) { ["name"] = "not-a-guid" });

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => service.Object.DeleteFile(new EntityReference("account", id), "name"));
        Assert.Contains("did not read back as a file id", ex.Message);
    }
}
