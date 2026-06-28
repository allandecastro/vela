using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Files;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class FileColumnExtensionsTests
    {
        [Fact]
        public void UploadThenDownload_RoundTripsContent_AcrossMultipleBlocks()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            EntityReference target = new EntityReference("account", id);
            byte[] content = Enumerable.Range(0, 25).Select(i => (byte)i).ToArray();

            // A small block size forces the helper to chunk (and the download to loop).
            Guid fileId = crm.UploadFile(target, "vela_document", "doc.bin", content, blockSize: 8);

            // The column stores the fileid; the bytes come back through the download helper.
            Guid stored = crm.Retrieve("account", id, new ColumnSet("vela_document")).GetAttributeValue<Guid>("vela_document");
            Assert.Equal(fileId, stored);
            Assert.Equal(content, crm.DownloadFile(target, "vela_document", blockSize: 8));
        }

        [Fact]
        public void DeleteFile_ByColumn_ClearsTheColumn()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            EntityReference target = new EntityReference("account", id);
            crm.UploadFile(target, "vela_document", "doc.bin", new byte[] { 1, 2, 3 });

            crm.DeleteFile(target, "vela_document");

            Assert.False(crm.Retrieve("account", id, new ColumnSet("vela_document")).Contains("vela_document"));
        }

        [Fact]
        public void DeleteFile_ByColumn_WhenNoFileSet_DoesNothing()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });

            // No exception when the column is empty.
            crm.DeleteFile(new EntityReference("account", id), "vela_document");
        }
    }
}
