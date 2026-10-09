using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Files;
using Xunit;

namespace Vela.Testing.Tests
{
    // #458: the platform keeps only a thumbnail for an Image column that does not store full images, so the block
    // download fails with 0x80040217 (verified live). The harness enforces that once the column's metadata says so,
    // and keeps serving the stored bytes when no metadata is registered.
    public sealed class ImageDownloadFidelityTests
    {
        // 0x80040217 ObjectDoesNotExist.
        private const int ObjectDoesNotExist = unchecked((int)0x80040217);

        private static readonly byte[] Picture = { 42, 43, 44, 45 };

        private static (InMemoryOrganizationService Svc, EntityReference Target) WithImage(bool? canStoreFullImage)
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();
            if (canStoreFullImage.HasValue)
            {
                svc.WithEntityMetadata(FakeEntityMetadata.For("account")
                    .PrimaryId("accountid")
                    .Image("new_img", canStoreFullImage.Value)
                    .Build());
            }

            Guid id = svc.Create(new Entity("account") { ["name"] = "Zava", ["new_img"] = Picture });
            return (svc, new EntityReference("account", id));
        }

        [Fact]
        public void DownloadFile_ImageColumnWithoutFullImages_ThrowsObjectDoesNotExist()
        {
            (InMemoryOrganizationService svc, EntityReference target) = WithImage(canStoreFullImage: false);

            FaultException<OrganizationServiceFault> fault =
                Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.DownloadFile(target, "new_img"));

            Assert.Equal(ObjectDoesNotExist, fault.Detail.ErrorCode);
            Assert.Contains("No FileAttachment records found", fault.Detail.Message);
            Assert.Contains("image attribute: new_img of account record with id " + target.Id.ToString("D"), fault.Detail.Message);
        }

        [Fact]
        public void DownloadFile_ImageColumnWithFullImages_ReturnsTheBytesSet()
        {
            (InMemoryOrganizationService svc, EntityReference target) = WithImage(canStoreFullImage: true);

            Assert.Equal(Picture, svc.DownloadFile(target, "new_img"));
        }

        [Fact]
        public void DownloadFile_NoMetadataRegistered_KeepsServingTheStoredBytes()
        {
            (InMemoryOrganizationService svc, EntityReference target) = WithImage(canStoreFullImage: null);

            Assert.Equal(Picture, svc.DownloadFile(target, "new_img"));
        }

        [Fact]
        public void DownloadFile_MetadataWithoutTheColumn_KeepsServingTheStoredBytes()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account").PrimaryId("accountid").Build());
            Guid id = svc.Create(new Entity("account") { ["name"] = "Zava", ["new_img"] = Picture });

            Assert.Equal(Picture, svc.DownloadFile(new EntityReference("account", id), "new_img"));
        }

        [Fact]
        public void Image_DescribesAnImageColumn()
        {
            EntityMetadata metadata = FakeEntityMetadata.For("account").Image("new_img", canStoreFullImage: true).Build();

            ImageAttributeMetadata image = Assert.IsType<ImageAttributeMetadata>(metadata.Attributes.Single());
            Assert.Equal("new_img", image.LogicalName);
            Assert.Equal(AttributeTypeCode.Virtual, image.AttributeType);
            Assert.True(image.CanStoreFullImage);
        }
    }
}
