using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Vela.Files;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // #303 Tier-0: a File-column download must respect the same access control as Retrieve - a record-level Read
    // demand and field-level security on the column - so an access-control test cannot pass green in the harness
    // while the same read would be blocked on a real org.
    public sealed class FileDownloadSecurityTests
    {
        // 0x80040220 PrincipalPrivilegeDenied - the platform's access-denied fault code.
        private const int PrincipalPrivilegeDenied = unchecked((int)0x80040220);

        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000001");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000000b1");

        // Create a record and upload a file to it with full access (no security yet); security is applied afterwards
        // so the download runs under the restricted role.
        private static (InMemoryOrganizationService Svc, EntityReference Target) SeededWithFile()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();
            Guid id = svc.Create(new Entity("account") { ["name"] = "Acme" });
            EntityReference target = new EntityReference("account", id);
            svc.UploadFile(target, "new_file", "f.bin", new byte[] { 1, 2, 3 });
            return (svc, target);
        }

        [Fact]
        public void Download_is_denied_without_the_read_privilege()
        {
            (InMemoryOrganizationService svc, EntityReference target) = SeededWithFile();
            svc.WithCurrentUser(Alice).WithSecurity(s => s
                .BusinessUnit(SalesBu)
                .Role("writer", r => r.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Write))
                .User(Alice, SalesBu, "writer"));

            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.DownloadFile(target, "new_file"));
            Assert.Equal(PrincipalPrivilegeDenied, ex.Detail.ErrorCode);
        }

        [Fact]
        public void Download_is_denied_for_a_field_secured_column_without_field_read()
        {
            (InMemoryOrganizationService svc, EntityReference target) = SeededWithFile();
            svc.WithCurrentUser(Alice).WithSecurity(s => s
                .BusinessUnit(SalesBu)
                .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                .User(Alice, SalesBu, "reader")
                .SecureColumn("account", "new_file"));

            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.DownloadFile(target, "new_file"));
            Assert.Equal(PrincipalPrivilegeDenied, ex.Detail.ErrorCode);
        }

        [Fact]
        public void Download_succeeds_with_read_and_field_read_on_a_secured_column()
        {
            (InMemoryOrganizationService svc, EntityReference target) = SeededWithFile();
            svc.WithCurrentUser(Alice).WithSecurity(s => s
                .BusinessUnit(SalesBu)
                .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                .User(Alice, SalesBu, "reader")
                .SecureColumn("account", "new_file")
                .FieldPermission(Alice, "account", "new_file", read: true));

            byte[] content = svc.DownloadFile(target, "new_file");

            Assert.Equal(new byte[] { 1, 2, 3 }, content);
        }
    }
}
