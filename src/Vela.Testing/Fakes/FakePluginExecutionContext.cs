using System;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing
{
    /// <summary>
    /// A hand-written, fully mutable <see cref="IPluginExecutionContext7"/> for unit tests — no mocking library
    /// required. Implements the full context interface chain (IPluginExecutionContext2..7), so plugins that read
    /// newer members (Azure AD ids, environment/tenant, bulk image collections, IsApplicationUser, …) work.
    /// All members are settable and the collections are pre-initialized. Identity Guids default to fresh values.
    /// </summary>
    public sealed class FakePluginExecutionContext : IPluginExecutionContext7
    {
        /// <summary>Initializes a context with sensible defaults (Update / PostOperation / synchronous / depth 1 / account).</summary>
        public FakePluginExecutionContext()
        {
            MessageName = "Update";
            Stage = 40;
            Mode = 0;
            Depth = 1;
            PrimaryEntityName = "account";
            SecondaryEntityName = string.Empty;
            OrganizationName = string.Empty;
            InitiatingUserAgent = string.Empty;
            EnvironmentId = string.Empty;
            IsInTransaction = true;
            UserId = Guid.NewGuid();
            InitiatingUserId = Guid.NewGuid();
            CorrelationId = Guid.NewGuid();
            OrganizationId = Guid.NewGuid();
            BusinessUnitId = Guid.NewGuid();
            PrimaryEntityId = Guid.NewGuid();
            InputParameters = new ParameterCollection();
            OutputParameters = new ParameterCollection();
            SharedVariables = new ParameterCollection();
            PreEntityImages = new EntityImageCollection();
            PostEntityImages = new EntityImageCollection();
            PreEntityImagesCollection = Array.Empty<EntityImageCollection>();
            PostEntityImagesCollection = Array.Empty<EntityImageCollection>();
        }

        // --- IExecutionContext ---
        public int Mode { get; set; }
        public int IsolationMode { get; set; }
        public int Depth { get; set; }
        public string MessageName { get; set; }
        public string PrimaryEntityName { get; set; }
        public Guid? RequestId { get; set; }
        public string SecondaryEntityName { get; set; }
        public ParameterCollection InputParameters { get; set; }
        public ParameterCollection OutputParameters { get; set; }
        public ParameterCollection SharedVariables { get; set; }
        public Guid UserId { get; set; }
        public Guid InitiatingUserId { get; set; }
        public Guid BusinessUnitId { get; set; }
        public Guid OrganizationId { get; set; }
        public string OrganizationName { get; set; }
        public Guid PrimaryEntityId { get; set; }
        public EntityImageCollection PreEntityImages { get; set; }
        public EntityImageCollection PostEntityImages { get; set; }
        public EntityReference? OwningExtension { get; set; }
        public Guid CorrelationId { get; set; }
        public bool IsExecutingOffline { get; set; }
        public bool IsOfflinePlayback { get; set; }
        public bool IsInTransaction { get; set; }
        public Guid OperationId { get; set; }
        public DateTime OperationCreatedOn { get; set; }

        // --- IPluginExecutionContext ---
        public int Stage { get; set; }
        public IPluginExecutionContext? ParentContext { get; set; }

        // --- IPluginExecutionContext2 ---
        public Guid UserAzureActiveDirectoryObjectId { get; set; }
        public Guid InitiatingUserAzureActiveDirectoryObjectId { get; set; }
        public Guid InitiatingUserApplicationId { get; set; }
        public Guid PortalsContactId { get; set; }
        public bool IsPortalsClientCall { get; set; }

        // --- IPluginExecutionContext3 ---
        public Guid AuthenticatedUserId { get; set; }

        // --- IPluginExecutionContext4 (bulk CreateMultiple/UpdateMultiple image collections, by target index) ---
        public EntityImageCollection[] PreEntityImagesCollection { get; set; }
        public EntityImageCollection[] PostEntityImagesCollection { get; set; }

        // --- IPluginExecutionContext5 ---
        public string InitiatingUserAgent { get; set; }

        // --- IPluginExecutionContext6 ---
        public string EnvironmentId { get; set; }
        public Guid TenantId { get; set; }

        // --- IPluginExecutionContext7 ---
        public bool IsApplicationUser { get; set; }

        // The context as an operation it issues sees it in ParentContext: a copy whose SharedVariables are frozen as
        // they are now, so the variables the plugin sets afterwards do not reach it (verified live).
        internal FakePluginExecutionContext SnapshotForChild()
        {
            FakePluginExecutionContext copy = (FakePluginExecutionContext)MemberwiseClone();
            copy.SharedVariables = PluginPipeline.CopyParameters(SharedVariables);
            return copy;
        }
    }
}
