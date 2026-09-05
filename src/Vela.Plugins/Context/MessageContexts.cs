using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Crm.Sdk.Messages;

namespace Vela.Context
{
    // Per-message typed contexts. Each is a two-line specialization of StepContext / IStepContext with the
    // message's SDK request/response types. TEntity ties the context to the step's entity (type-state), so a
    // handler declared for the wrong message does not compile. Adding a message = one interface + one class.

    /// <summary>Typed context for a Create step (<c>ctx.Request.Target</c>, <c>ctx.Response.id</c>).</summary>
    public interface ICreateContext<TEntity> : IStepContext<CreateRequest, CreateResponse> where TEntity : Entity { }
    /// <summary>Typed context for an Update step.</summary>
    public interface IUpdateContext<TEntity> : IStepContext<UpdateRequest, UpdateResponse> where TEntity : Entity { }
    /// <summary>Typed context for a Delete step (<c>ctx.Request.Target</c> is an EntityReference).</summary>
    public interface IDeleteContext<TEntity> : IStepContext<DeleteRequest, DeleteResponse> where TEntity : Entity { }
    /// <summary>Typed context for a CreateMultiple step (<c>ctx.Request.Targets</c>, <c>ctx.Response.Ids</c>).</summary>
    public interface ICreateMultipleContext<TEntity> : IStepContext<CreateMultipleRequest, CreateMultipleResponse> where TEntity : Entity { }
    /// <summary>Typed context for an UpdateMultiple step (<c>ctx.Request.Targets</c>).</summary>
    public interface IUpdateMultipleContext<TEntity> : IStepContext<UpdateMultipleRequest, UpdateMultipleResponse> where TEntity : Entity { }
    /// <summary>Typed context for a Retrieve step (<c>ctx.Request.ColumnSet</c>, <c>ctx.Response.Entity</c>).</summary>
    public interface IRetrieveContext<TEntity> : IStepContext<RetrieveRequest, RetrieveResponse> where TEntity : Entity { }
    /// <summary>Typed context for a RetrieveMultiple step (<c>ctx.Request.Query</c>).</summary>
    public interface IRetrieveMultipleContext<TEntity> : IStepContext<RetrieveMultipleRequest, RetrieveMultipleResponse> where TEntity : Entity { }
    /// <summary>Typed context for an Associate step (<c>ctx.Request.Relationship</c>, <c>RelatedEntities</c>).</summary>
    public interface IAssociateContext<TEntity> : IStepContext<AssociateRequest, AssociateResponse> where TEntity : Entity { }
    /// <summary>Typed context for a Disassociate step.</summary>
    public interface IDisassociateContext<TEntity> : IStepContext<DisassociateRequest, DisassociateResponse> where TEntity : Entity { }
    /// <summary>Typed context for a Merge step (<c>ctx.Request.SubordinateId</c>, <c>UpdateContent</c>…).</summary>
    public interface IMergeContext<TEntity> : IStepContext<MergeRequest, MergeResponse> where TEntity : Entity { }
    /// <summary>Typed context for a GrantAccess step (<c>ctx.Request.PrincipalAccess</c>).</summary>
    public interface IGrantAccessContext<TEntity> : IStepContext<GrantAccessRequest, GrantAccessResponse> where TEntity : Entity { }
    /// <summary>Typed context for a ModifyAccess step.</summary>
    public interface IModifyAccessContext<TEntity> : IStepContext<ModifyAccessRequest, ModifyAccessResponse> where TEntity : Entity { }
    /// <summary>Typed context for a RevokeAccess step (<c>ctx.Request.Revokee</c>).</summary>
    public interface IRevokeAccessContext<TEntity> : IStepContext<RevokeAccessRequest, RevokeAccessResponse> where TEntity : Entity { }

    /// <summary>Runtime implementation of <see cref="ICreateContext{TEntity}"/>.</summary>
    public sealed class CreateContext<TEntity> : StepContext<CreateRequest, CreateResponse>, ICreateContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreateContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IUpdateContext{TEntity}"/>.</summary>
    public sealed class UpdateContext<TEntity> : StepContext<UpdateRequest, UpdateResponse>, IUpdateContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdateContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IDeleteContext{TEntity}"/>.</summary>
    public sealed class DeleteContext<TEntity> : StepContext<DeleteRequest, DeleteResponse>, IDeleteContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DeleteContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="ICreateMultipleContext{TEntity}"/>.</summary>
    public sealed class CreateMultipleContext<TEntity> : StepContext<CreateMultipleRequest, CreateMultipleResponse>, ICreateMultipleContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public CreateMultipleContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IUpdateMultipleContext{TEntity}"/>.</summary>
    public sealed class UpdateMultipleContext<TEntity> : StepContext<UpdateMultipleRequest, UpdateMultipleResponse>, IUpdateMultipleContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public UpdateMultipleContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IRetrieveContext{TEntity}"/>.</summary>
    public sealed class RetrieveContext<TEntity> : StepContext<RetrieveRequest, RetrieveResponse>, IRetrieveContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IRetrieveMultipleContext{TEntity}"/>.</summary>
    public sealed class RetrieveMultipleContext<TEntity> : StepContext<RetrieveMultipleRequest, RetrieveMultipleResponse>, IRetrieveMultipleContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RetrieveMultipleContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IAssociateContext{TEntity}"/>.</summary>
    public sealed class AssociateContext<TEntity> : StepContext<AssociateRequest, AssociateResponse>, IAssociateContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public AssociateContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IDisassociateContext{TEntity}"/>.</summary>
    public sealed class DisassociateContext<TEntity> : StepContext<DisassociateRequest, DisassociateResponse>, IDisassociateContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public DisassociateContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IMergeContext{TEntity}"/>.</summary>
    public sealed class MergeContext<TEntity> : StepContext<MergeRequest, MergeResponse>, IMergeContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public MergeContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IGrantAccessContext{TEntity}"/>.</summary>
    public sealed class GrantAccessContext<TEntity> : StepContext<GrantAccessRequest, GrantAccessResponse>, IGrantAccessContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public GrantAccessContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IModifyAccessContext{TEntity}"/>.</summary>
    public sealed class ModifyAccessContext<TEntity> : StepContext<ModifyAccessRequest, ModifyAccessResponse>, IModifyAccessContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public ModifyAccessContext(ILocalPluginContext inner) : base(inner) { } }

    /// <summary>Runtime implementation of <see cref="IRevokeAccessContext{TEntity}"/>.</summary>
    public sealed class RevokeAccessContext<TEntity> : StepContext<RevokeAccessRequest, RevokeAccessResponse>, IRevokeAccessContext<TEntity> where TEntity : Entity
    { /// <summary>Wraps the runtime context.</summary><param name="inner">The runtime plugin context.</param>
        public RevokeAccessContext(ILocalPluginContext inner) : base(inner) { } }
}
