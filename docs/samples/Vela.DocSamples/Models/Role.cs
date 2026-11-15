namespace Vela.DocSamples.Models
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Client;

    // A minimal early-bound model, the shape `vela models` generates.
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class Role : Entity
    {
        public const string EntityLogicalNameValue = "role";

        public Role()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("roleid")]
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

        [AttributeLogicalName("parentrootroleid")]
        public EntityReference ParentRootRoleId
        {
            get => GetAttributeValue<EntityReference>("parentrootroleid");
            set => SetAttributeValue("parentrootroleid", value);
        }
    }
}
