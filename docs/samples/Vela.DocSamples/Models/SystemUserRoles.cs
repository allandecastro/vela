namespace Vela.DocSamples.Models
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Client;

    // A minimal early-bound model of the user/role intersect table, the shape `vela models --intersect` generates.
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class SystemUserRoles : Entity
    {
        public const string EntityLogicalNameValue = "systemuserroles";

        public SystemUserRoles()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("systemuserroleid")]
        public override Guid Id
        {
            get => base.Id;
            set => base.Id = value;
        }

        [AttributeLogicalName("roleid")]
        public Guid? RoleId
        {
            get => GetAttributeValue<Guid?>("roleid");
            set => SetAttributeValue("roleid", value);
        }

        [AttributeLogicalName("systemuserid")]
        public Guid? SystemUserId
        {
            get => GetAttributeValue<Guid?>("systemuserid");
            set => SetAttributeValue("systemuserid", value);
        }
    }
}
