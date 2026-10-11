namespace Vela.StepApiGen;

/// <summary>Emits src/Vela.Plugins/Steps/StepApi.cs: the public type-state interfaces of the step builder.</summary>
internal static class StepApiEmitter
{
    public const string RelativePath = "src/Vela.Plugins/Steps/StepApi.cs";

    public static string Emit()
    {
        var w = new CodeWriter();
        w.Header("Step builder API (Form C).");
        w.Block(Preamble);

        // Per message x stage: the terminal config interface.
        foreach (var message in MessageModel.Messages)
        {
            foreach (var stage in StageExtensions.All)
            {
                WriteConfig(w, StepConfig.ForMessage(message, stage));
            }
        }

        // Per message: the stage selector returned by On{Message}().
        foreach (var message in MessageModel.Messages)
        {
            WriteStages(w,
                $"Stage choice for On{message.Name}(). Pre-stages are Sync-only; PostOperation is Sync/Async.",
                StepConfig.StagesType(message.Name),
                " where TEntity : Entity",
                stage => StepConfig.ConfigType(message.Name, stage));
        }

        // The permissive OnMessage<TMessage>() surface: universal image rules, untyped or request-typed handlers.
        WriteStages(w,
            "Stage choice for <c>OnMessage&lt;TMessage&gt;()</c>: a custom / non-modelled message. Universal image rules only.",
            "IPermissiveStages<TEntity>",
            " where TEntity : Entity",
            stage => $"IPermissive{stage}Config<TEntity>");
        foreach (var stage in StageExtensions.All)
        {
            WriteConfig(w, StepConfig.ForPermissive(stage));
        }

        // The typed On<TRequest>() surface (implemented by the hand-written TypedStepAdapter.cs).
        WriteStages(w,
            "Stage choice for On&lt;TRequest&gt;(): the handler is pre-typed to TRequest.",
            "ITypedStages<TEntity, TRequest>",
            " where TEntity : Entity where TRequest : OrganizationRequest, new()",
            stage => $"ITyped{stage}Config<TEntity, TRequest>");
        w.Block(AsyncOnlyTypedStages);
        foreach (var stage in StageExtensions.All)
        {
            WriteConfig(w, StepConfig.ForTyped(stage));
        }

        // The entry point.
        w.Line("    /// <summary>Entry point returned by RegisterStep(): pick the message the step reacts to.</summary>");
        w.Line("    public interface IStepTrigger<TEntity> : IFluentStep where TEntity : Entity");
        w.Line("    {");
        foreach (var message in MessageModel.StepTriggerOrder())
        {
            w.Line($"        {StepConfig.StagesType(message.Name)} On{message.Name}();");
        }
        w.Block(StepTriggerTail);
        return w.ToString();
    }

    private static void WriteConfig(CodeWriter w, StepConfig config)
    {
        w.Line($"    /// <summary>{config.Summary}</summary>");
        w.Line($"    public interface {config.Type} : IStepConfig<TEntity>{config.Constraints}");
        w.Line("    {");
        foreach (var modifier in config.Modifiers())
        {
            foreach (var doc in modifier.Doc ?? [])
            {
                w.Line($"        /// {doc}");
            }
            w.Line($"        {config.Type} {modifier.Signature};");
        }
        foreach (var handle in config.Handles)
        {
            w.Line($"        void Handle{handle.TypeParameters}({handle.Parameter}){handle.Constraints};");
        }
        w.Line("    }");
        w.Line();
    }

    private static void WriteStages(CodeWriter w, string summary, string type, string constraints, Func<Stage, string> configType)
    {
        w.Line($"    /// <summary>{summary}</summary>");
        w.Line($"    public interface {type} : IFluentStep{constraints}");
        w.Line("    {");
        foreach (var stage in StageExtensions.All)
        {
            w.Line($"        {stage.ModeInterface()}<{configType(stage)}> {stage}();");
        }
        w.Line("    }");
        w.Line();
    }

    private const string Preamble = """
        using System;
        using System.ComponentModel;
        using System.Linq.Expressions;
        using Vela.Constants;
        using Vela.Context;
        using Microsoft.Xrm.Sdk;

        namespace Vela.Steps
        {
            /// <summary>Base for every fluent builder interface: hides the object members (ToString/Equals/GetHashCode/
            /// GetType) from IntelliSense so the completion popup shows only the builder's own steps. VS and Rider honor
            /// [EditorBrowsable(Never)] for members from a referenced package (Vela.Plugins is one).</summary>
            [EditorBrowsable(EditorBrowsableState.Never)]
            public interface IFluentStep
            {
                [EditorBrowsable(EditorBrowsableState.Never)] Type GetType();
                [EditorBrowsable(EditorBrowsableState.Never)] int GetHashCode();
                [EditorBrowsable(EditorBrowsableState.Never)] string ToString();
                [EditorBrowsable(EditorBrowsableState.Never)] bool Equals(object obj);
            }

            /// <summary>
            /// Base for every terminal step config. Carries the <c>Handle(Delegate)</c> overload that binds a handler taking
            /// the generated per-step context (<c>{Handler}Context</c>, a subclass of the concrete step context). A method
            /// group converts to <see cref="Delegate"/> via its natural type, so the developer writes <c>.Handle(OnX)</c>
            /// with no explicit type; the runtime builds the handler's declared context. Each config also keeps its own
            /// strongly-typed <c>Handle(Action&lt;I...Context&gt;)</c> overload for a handler taking the plain message context.
            /// </summary>
            public interface IStepConfig<TEntity> : IFluentStep where TEntity : Entity
            {
                void Handle(Delegate handler);
            }

            /// <summary>Sync-only mode choice (pre-validation / pre-operation): async is PostOperation-only in Dataverse.</summary>
            public interface ISyncMode<out TConfig> : IFluentStep { TConfig Sync(); }
            /// <summary>Sync or async mode choice (post-operation only).</summary>
            public interface ISyncAsyncMode<out TConfig> : IFluentStep
            {
                TConfig Sync();
                TConfig Async();
                /// <summary>Asynchronous; when <paramref name="deleteJobOnSuccess"/> is true, the system job is deleted once it succeeds (Dataverse asyncautodelete).</summary>
                TConfig Async(bool deleteJobOnSuccess);
            }
            /// <summary>Async-only mode choice (a Custom API whose allowedcustomprocessingsteptype is AsyncOnly).</summary>
            public interface IAsyncMode<out TConfig> : IFluentStep
            {
                TConfig Async();
                /// <summary>Asynchronous; when <paramref name="deleteJobOnSuccess"/> is true, the system job is deleted once it succeeds (Dataverse asyncautodelete).</summary>
                TConfig Async(bool deleteJobOnSuccess);
            }

        """;

    private const string AsyncOnlyTypedStages = """
            /// <summary>Async-only stage choice for a Custom API (AsyncOnly). Reached via the generated On&lt;Message&gt;() extension, not by hand.</summary>
            /// <remarks>Only PostOperation is offered: an asynchronous step is PostOperation-only in Dataverse, so a pre-stage async step would be rejected at deploy time.</remarks>
            [EditorBrowsable(EditorBrowsableState.Never)]
            public interface IAsyncOnlyTypedStages<TEntity, TRequest> : IFluentStep where TEntity : Entity where TRequest : OrganizationRequest, new()
            {
                IAsyncMode<ITypedPostOperationConfig<TEntity, TRequest>> PostOperation();
            }

        """;

    private const string StepTriggerTail = """
                /// <summary>Trigger on a custom / non-modelled message stated by a marker type ([CustomMessage("...")]) or a request type. Image validity is enforced at runtime.</summary>
                IPermissiveStages<TEntity> OnMessage<TMessage>() where TMessage : class;
                /// <summary>Trigger on the message named by TRequest (its RequestName); the handler is pre-typed to TRequest.</summary>
                ITypedStages<TEntity, TRequest> On<TRequest>() where TRequest : OrganizationRequest, new();
                /// <summary>Async-only typed trigger for a Custom API (AsyncOnly). Reached via the generated On&lt;Message&gt;() extension, not by hand.</summary>
                [EditorBrowsable(EditorBrowsableState.Never)]
                IAsyncOnlyTypedStages<TEntity, TRequest> OnAsyncOnly<TRequest>() where TRequest : OrganizationRequest, new();
            }
        }
        """;
}
